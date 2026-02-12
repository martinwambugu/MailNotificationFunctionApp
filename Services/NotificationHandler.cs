using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
using MailNotificationFunctionApp.Extensions; // ✅ ADD THIS  
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Services
{
    /// <summary>  
    /// Handles incoming Microsoft Graph webhook notifications for email changes.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This handler validates notifications, persists them to PostgreSQL, and publishes them to RabbitMQ  
    /// for asynchronous processing by downstream consumers.  
    /// </para>  
    /// <para>  
    /// <b>Security:</b> Validates client state to prevent webhook spoofing attacks.  
    /// </para>  
    /// </remarks>  
    public class NotificationHandler : INotificationHandler
    {
        private readonly IEmailNotificationRepository _repo;
        private readonly IDbConnectionFactory _dbFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<NotificationHandler> _logger;
        private readonly ICustomTelemetry _telemetry;
        private readonly IMessageQueuePublisher _queuePublisher;
        private readonly SubscriptionValidationConfiguration _validationConfig;

        /// <summary>  
        /// Initializes a new instance of the <see cref="NotificationHandler"/> class.  
        /// </summary>  
        public NotificationHandler(
            IEmailNotificationRepository repo,
            IDbConnectionFactory dbFactory,
            IConfiguration config,
            ILogger<NotificationHandler> logger,
            ICustomTelemetry telemetry,
            IMessageQueuePublisher queuePublisher,
            IOptions<SubscriptionValidationConfiguration> validationOptions)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _queuePublisher = queuePublisher ?? throw new ArgumentNullException(nameof(queuePublisher));

            if (validationOptions == null) throw new ArgumentNullException(nameof(validationOptions));
            _validationConfig = validationOptions.Value ?? new SubscriptionValidationConfiguration();
        }

        /// <inheritdoc/>  
        /// <summary>  
        /// ✅ FIXED: Now accepts NotificationItem instead of GraphNotification.ChangeNotification  
        /// </summary>  
        public async Task<bool> HandleAsync(
            NotificationItem notification, // ✅ CHANGED FROM GraphNotification.ChangeNotification  
            string rawJson,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(notification);

            _logger.LogInformation(
                "📬 Handling notification: SubscriptionId={SubscriptionId}, ChangeType={ChangeType}, Resource={Resource}",
                notification.SubscriptionId,
                notification.ChangeType,
                notification.Resource);

            // ✅ Validate client state to prevent spoofing (skip if null - retry scenario)  
            if (!string.IsNullOrWhiteSpace(notification.ClientState))
            {
                if (!await ValidateClientStateAsync(notification, cancellationToken))
                {
                    _logger.LogWarning(
                        "🚨 Client state validation failed for SubscriptionId: {SubscriptionId}",
                        notification.SubscriptionId);

                    _telemetry.TrackEvent("MailNotification_ValidationFailed", new Dictionary<string, string>
                    {
                        { "SubscriptionId", notification.SubscriptionId ?? "unknown" },
                        { "Reason", "InvalidClientState" }
                    });

                    throw new SecurityException(
                        $"Client state validation failed for subscription {notification.SubscriptionId}. " +
                        "Possible webhook spoofing attempt.");
                }
            }
            else
            {
                _logger.LogInformation(
                    "ℹ️ Skipping client state validation (retry scenario). SubscriptionId: {SubscriptionId}",
                    notification.SubscriptionId);
            }

            // ✅ Extract userId and messageId from resource URI  
            var (userId, messageId) = ExtractResourceDetails(notification.Resource);

            _logger.LogInformation(
                "📋 Extracted details: UserId={UserId}, MessageId={MessageId}",
                userId, messageId);

            // ✅ Create notification entity  
            var entity = new EmailNotification
            {
                NotificationId = notification.Id ?? Guid.NewGuid().ToString(),
                SubscriptionId = notification.SubscriptionId ?? "unknown",
                ChangeType = notification.ChangeType ?? "created",
                ResourceUri = notification.Resource ?? string.Empty,
                ResourceId = notification.ResourceData?.Id ?? messageId, // Use ResourceData.Id or fallback  
                NotificationDateTime = notification.SubscriptionExpirationDateTime ?? DateTime.UtcNow,
                ReceivedDateTime = DateTime.UtcNow,
                RawNotificationPayload = rawJson,
                ProcessingStatus = "Pending",
                RetryCount = 0,
                ErrorMessage = null
            };

            _telemetry.TrackEvent("MailNotification_Received", new Dictionary<string, string>
            {
                { "NotificationId", entity.NotificationId },
                { "SubscriptionId", entity.SubscriptionId },
                { "ResourceId", entity.ResourceId ?? "unknown" },
                { "ChangeType", entity.ChangeType },
                { "UserId", userId },
                { "MessageId", messageId }
            });

            try
            {
                // ✅ Step 1: Save notification to database  
                _logger.LogInformation(
                    "💾 Saving notification to database: {NotificationId}",
                    entity.NotificationId);

                await _repo.SaveNotificationAsync(entity, cancellationToken);

                _logger.LogInformation(
                    "✅ Notification saved to database: {NotificationId}",
                    entity.NotificationId);

                _telemetry.TrackEvent("MailNotification_DatabaseSaved", new Dictionary<string, string>
                {
                    { "NotificationId", entity.NotificationId }
                });

                // ✅ Step 2: Publish to RabbitMQ  
                _logger.LogInformation(
                    "📤 Publishing notification to queue: {NotificationId}",
                    entity.NotificationId);

                var queueMessage = new NotificationQueueMessage
                {
                    NotificationId = entity.NotificationId,
                    UserId = userId,
                    MessageId = messageId,
                    ChangeType = entity.ChangeType,
                    SubscriptionId = entity.SubscriptionId,
                    QueuedAt = DateTime.UtcNow
                };

                var published = await _queuePublisher.PublishNotificationAsync(queueMessage, cancellationToken);

                if (!published)
                {
                    _logger.LogWarning(
                        "⚠️ Failed to publish notification to queue: NotificationId={NotificationId}",
                        entity.NotificationId);

                    // ✅ Update status to indicate queue failure (non-critical)  
                    entity.ProcessingStatus = "Failed";
                    entity.ErrorMessage = "Failed to publish to message queue";
                    await _repo.SaveNotificationAsync(entity, cancellationToken);

                    _telemetry.TrackEvent("MailNotification_QueuePublishFailed", new Dictionary<string, string>
                    {
                        { "NotificationId", entity.NotificationId },
                        { "Reason", "PublishReturnedFalse" }
                    });

                    // ✅ Return false to trigger retry  
                    return false;
                }

                _logger.LogInformation(
                    "✅ Notification published to queue successfully: {NotificationId}",
                    entity.NotificationId);

                _telemetry.TrackEvent("MailNotification_QueuePublished", new Dictionary<string, string>
                {
                    { "NotificationId", entity.NotificationId }
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Error handling notification {NotificationId}: {ErrorMessage}",
                    entity.NotificationId,
                    ex.Message);

                // ✅ Update notification status to Failed  
                entity.ProcessingStatus = "Failed";
                entity.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                entity.RetryCount++;

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "NotificationId", entity.NotificationId },
                    { "Operation", "HandleAsync" },
                    { "ErrorType", ex.GetType().Name }
                });

                // ✅ Attempt to save error state (best effort)  
                try
                {
                    await _repo.SaveNotificationAsync(entity, cancellationToken);
                    _logger.LogInformation(
                        "📝 Saved error state for notification: {NotificationId}",
                        entity.NotificationId);
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(
                        saveEx,
                        "❌ Failed to save error state for notification {NotificationId}",
                        entity.NotificationId);

                    _telemetry.TrackException(saveEx, new Dictionary<string, string>
                    {
                        { "NotificationId", entity.NotificationId },
                        { "Operation", "SaveErrorState" }
                    });
                }

                // ✅ Return false to trigger retry (don't rethrow)  
                return false;
            }
        }

        /// <summary>  
        /// Extracts userId and messageId from Microsoft Graph resource URI.  
        /// </summary>  
        private (string userId, string messageId) ExtractResourceDetails(string? resourceUri)
        {
            if (string.IsNullOrWhiteSpace(resourceUri))
            {
                _logger.LogWarning("⚠️ Resource URI is null or empty");
                return ("unknown", "unknown");
            }

            try
            {
                var parts = resourceUri.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var userId = "unknown";
                var messageId = "unknown";

                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Equals("Users", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                    {
                        userId = parts[i + 1];
                    }
                    else if (parts[i].Equals("Messages", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                    {
                        messageId = parts[i + 1];
                    }
                }

                if (userId == "unknown" || messageId == "unknown")
                {
                    _logger.LogWarning(
                        "⚠️ Could not extract complete details from resource URI: {ResourceUri}",
                        resourceUri);
                }

                return (userId, messageId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "⚠️ Failed to parse resource URI: {ResourceUri}",
                    resourceUri);

                return ("unknown", "unknown");
            }
        }

        /// <summary>  
        /// Validates the client state from the notification against the stored subscription.  
        /// </summary>  
        private async Task<bool> ValidateClientStateAsync(
            NotificationItem notification,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(notification.ClientState))
            {
                _logger.LogWarning("⚠️ Notification missing client state");
                return false;
            }

            if (string.IsNullOrWhiteSpace(notification.SubscriptionId))
            {
                _logger.LogWarning("⚠️ Notification missing subscription ID");
                return false;
            }

            try
            {
                const string sql = @"  
                    SELECT subscriptionid, subscriptionexpirationtime, clientstate  
                    FROM mailsubscriptions  
                    WHERE subscriptionid = @SubscriptionId;";

                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                var command = new CommandDefinition(
                    sql,
                    new { SubscriptionId = notification.SubscriptionId },
                    cancellationToken: cancellationToken,
                    commandTimeout: 30);

                SubscriptionValidationRow? subscription = null;
                try
                {
                    subscription = await conn.QuerySingleOrDefaultAsync<SubscriptionValidationRow>(command);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(
                        "❌ Database query cancelled (timeout or cancellation) for SubscriptionId: {SubscriptionId}",
                        notification.SubscriptionId);
                    return false;
                }
                catch (NpgsqlException dbEx) when (
                    dbEx.InnerException is TimeoutException ||
                    dbEx.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                    dbEx.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        dbEx,
                        "❌ Database query timeout for SubscriptionId: {SubscriptionId}. " +
                        "This may indicate database performance issues or connection pool exhaustion.",
                        notification.SubscriptionId);
                    return false;
                }

                if (subscription is null)
                {
                    _logger.LogWarning(
                        "⚠️ Subscription not found in database: {SubscriptionId}",
                        notification.SubscriptionId);

                    _telemetry.TrackEvent("MailNotification_SubscriptionNotFound", new Dictionary<string, string>
                    {
                        { "SubscriptionId", notification.SubscriptionId },
                        { "Reason", "NotFound" }
                    });

                    return false;
                }

                var nowUtc = DateTime.UtcNow;
                var expiration = subscription.SubscriptionExpirationTime;
                var graceMinutes = _validationConfig.ExpirationGracePeriodMinutes;
                var allowExpiredWithinGrace = _validationConfig.AllowExpiredWithinGracePeriod;
                var threshold = graceMinutes > 0
                    ? nowUtc.AddMinutes(-graceMinutes)
                    : nowUtc;

                var isWithinValidityWindow = expiration > threshold;

                if (!isWithinValidityWindow)
                {
                    var timeSinceExpiry = nowUtc - expiration;

                    _logger.LogWarning(
                        "⚠️ Subscription expired and outside grace period. SubscriptionId: {SubscriptionId}, " +
                        "ExpiredAtUtc: {ExpiredAtUtc:O}, NowUtc: {NowUtc:O}, TimeSinceExpiry: {TimeSinceExpiry}",
                        notification.SubscriptionId,
                        expiration,
                        nowUtc,
                        timeSinceExpiry);

                    _telemetry.TrackEvent("MailNotification_SubscriptionExpired", new Dictionary<string, string>
                    {
                        { "SubscriptionId", notification.SubscriptionId },
                        { "ExpiredAtUtc", expiration.ToString("O") },
                        { "NowUtc", nowUtc.ToString("O") },
                        { "TimeSinceExpiry", timeSinceExpiry.ToString() }
                    });

                    return false;
                }

                // If we get here, the subscription is either not expired or within grace period
                if (expiration <= nowUtc && expiration > threshold && allowExpiredWithinGrace)
                {
                    var timeSinceExpiry = nowUtc - expiration;

                    _logger.LogInformation(
                        "ℹ️ Subscription expired but within grace period. Accepting notification. " +
                        "SubscriptionId: {SubscriptionId}, ExpiredAtUtc: {ExpiredAtUtc:O}, NowUtc: {NowUtc:O}, " +
                        "TimeSinceExpiry: {TimeSinceExpiry}, GraceMinutes: {GraceMinutes}",
                        notification.SubscriptionId,
                        expiration,
                        nowUtc,
                        timeSinceExpiry,
                        graceMinutes);

                    _telemetry.TrackEvent("MailNotification_SubscriptionExpiredWithinGracePeriod", new Dictionary<string, string>
                    {
                        { "SubscriptionId", notification.SubscriptionId },
                        { "ExpiredAtUtc", expiration.ToString("O") },
                        { "NowUtc", nowUtc.ToString("O") },
                        { "TimeSinceExpiry", timeSinceExpiry.ToString() },
                        { "GraceMinutes", graceMinutes.ToString() }
                    });
                }

                var expectedClientState = subscription.ClientState;

                if (string.IsNullOrWhiteSpace(expectedClientState))
                {
                    _logger.LogWarning(
                        "⚠️ Subscription record has empty client state: {SubscriptionId}",
                        notification.SubscriptionId);

                    _telemetry.TrackEvent("MailNotification_SubscriptionInvalid", new Dictionary<string, string>
                    {
                        { "SubscriptionId", notification.SubscriptionId },
                        { "Reason", "EmptyClientState" }
                    });

                    return false;
                }

                var isValid = CryptographicEquals(notification.ClientState, expectedClientState);

                if (!isValid)
                {
                    _logger.LogWarning(
                        "🚨 Client state mismatch for SubscriptionId: {SubscriptionId}",
                        notification.SubscriptionId);

                    _telemetry.TrackEvent("MailNotification_SubscriptionClientStateMismatch", new Dictionary<string, string>
                    {
                        { "SubscriptionId", notification.SubscriptionId }
                    });
                }

                return isValid;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(
                    "❌ Client state validation cancelled (timeout) for SubscriptionId: {SubscriptionId}",
                    notification.SubscriptionId);
                return false;
            }
            catch (Exception ex)
            {
                // Better error classification to distinguish timeout vs other errors
                var isTimeout = ex is TimeoutException ||
                               ex.InnerException is TimeoutException ||
                               ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                               ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase);

                if (isTimeout)
                {
                    _logger.LogError(
                        ex,
                        "❌ Database timeout during client state validation for SubscriptionId: {SubscriptionId}. " +
                        "This may indicate database performance issues, missing indexes, or connection pool exhaustion.",
                        notification.SubscriptionId);
                }
                else
                {
                    _logger.LogError(
                        ex,
                        "❌ Error validating client state for SubscriptionId: {SubscriptionId}",
                        notification.SubscriptionId);
                }
                return false;
            }
        }

        /// <summary>  
        /// Performs constant-time string comparison to prevent timing attacks.  
        /// </summary>  
        private static bool CryptographicEquals(string a, string b)
        {
            if (a == null || b == null)
                return false;

            if (a.Length != b.Length)
                return false;

            var result = 0;
            for (var i = 0; i < a.Length; i++)
            {
                result |= a[i] ^ b[i];
            }

            return result == 0;
        }

        /// <summary>
        /// Lightweight DTO used for subscription validation query.
        /// </summary>
        private sealed class SubscriptionValidationRow
        {
            public string SubscriptionId { get; set; } = string.Empty;
            public DateTime SubscriptionExpirationTime { get; set; }
            public string ClientState { get; set; } = string.Empty;
        }
    }
}