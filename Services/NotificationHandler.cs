using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
using MailNotificationFunctionApp.Extensions; // ✅ ADD THIS  
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

        /// <summary>  
        /// Initializes a new instance of the <see cref="NotificationHandler"/> class.  
        /// </summary>  
        public NotificationHandler(
            IEmailNotificationRepository repo,
            IDbConnectionFactory dbFactory,
            IConfiguration config,
            ILogger<NotificationHandler> logger,
            ICustomTelemetry telemetry,
            IMessageQueuePublisher queuePublisher)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _queuePublisher = queuePublisher ?? throw new ArgumentNullException(nameof(queuePublisher));
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
                    SELECT clientstate  
                    FROM mailsubscriptions  
                    WHERE subscriptionid = @SubscriptionId  
                    AND subscriptionexpirationtime > NOW();";

                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                var command = new CommandDefinition(
                    sql,
                    new { SubscriptionId = notification.SubscriptionId },
                    cancellationToken: cancellationToken,
                    commandTimeout: 10
                );

                var expectedClientState = await conn.QuerySingleOrDefaultAsync<string>(command);

                if (string.IsNullOrWhiteSpace(expectedClientState))
                {
                    _logger.LogWarning(
                        "⚠️ Subscription not found or expired: {SubscriptionId}",
                        notification.SubscriptionId);
                    return false;
                }

                var isValid = CryptographicEquals(notification.ClientState, expectedClientState);

                if (!isValid)
                {
                    _logger.LogWarning(
                        "🚨 Client state mismatch for SubscriptionId: {SubscriptionId}",
                        notification.SubscriptionId);
                }

                return isValid;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Error validating client state for SubscriptionId: {SubscriptionId}",
                    notification.SubscriptionId);
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
    }
}