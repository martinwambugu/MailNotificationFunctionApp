using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
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
    public class NotificationHandler : INotificationHandler
    {
        private readonly IEmailNotificationRepository _repo;
        private readonly IDbConnectionFactory _dbFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<NotificationHandler> _logger;
        private readonly ICustomTelemetry _telemetry;
        private readonly IMessageQueuePublisher _queuePublisher; // ✅ NEW  

        public NotificationHandler(
            IEmailNotificationRepository repo,
            IDbConnectionFactory dbFactory,
            IConfiguration config,
            ILogger<NotificationHandler> logger,
            ICustomTelemetry telemetry,
            IMessageQueuePublisher queuePublisher) // ✅ NEW  
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _queuePublisher = queuePublisher ?? throw new ArgumentNullException(nameof(queuePublisher)); // ✅ NEW  
        }

        public async Task<bool> HandleAsync(
            GraphNotification.ChangeNotification notification,
            string rawJson,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(notification);

            _logger.LogInformation(
                "📬 Handling notification: SubscriptionId={SubscriptionId}, ChangeType={ChangeType}",
                notification.SubscriptionId, notification.ChangeType);

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

            var entity = new EmailNotification
            {
                NotificationId = notification.Id ?? Guid.NewGuid().ToString(),
                SubscriptionId = notification.SubscriptionId ?? "unknown",
                ChangeType = notification.ChangeType ?? "created",
                ResourceUri = notification.Resource ?? string.Empty,
                ResourceId = notification.ResourceDataId ?? Guid.NewGuid().ToString(),
                NotificationDateTime = DateTime.UtcNow,
                ReceivedDateTime = DateTime.UtcNow,
                RawNotificationPayload = rawJson,
                ProcessingStatus = "Pending"
            };

            _telemetry.TrackEvent("MailNotification_Received", new Dictionary<string, string>
            {
                { "SubscriptionId", entity.SubscriptionId },
                { "ResourceId", entity.ResourceId },
                { "ChangeType", entity.ChangeType }
            });

            try
            {
                // ✅ Save notification to database  
                await _repo.SaveNotificationAsync(entity, cancellationToken);

                _telemetry.TrackEvent("MailNotification_Saved", new Dictionary<string, string>
                {
                    { "NotificationId", entity.NotificationId }
                });

                // ✅ NEW: Extract userId and messageId from resource URI  
                var (userId, messageId) = ExtractResourceDetails(notification.Resource);

                // ✅ NEW: Publish to RabbitMQ  
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
                    entity.ProcessingStatus = "SavedButNotQueued";
                    await _repo.SaveNotificationAsync(entity, cancellationToken);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error saving notification {NotificationId}", entity.NotificationId);

                entity.ProcessingStatus = "Failed";
                entity.ErrorMessage = ex.Message;
                entity.RetryCount++;

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "NotificationId", entity.NotificationId }
                });

                try
                {
                    await _repo.SaveNotificationAsync(entity, cancellationToken);
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "❌ Failed to save error state for notification {NotificationId}",
                        entity.NotificationId);
                }

                throw;
            }
        }

        /// <summary>  
        /// Extracts userId and messageId from Microsoft Graph resource URI.  
        /// </summary>  
        /// <remarks>  
        /// Example URI: "Users/{userId}/Messages/{messageId}"  
        /// </remarks>  
        private (string userId, string messageId) ExtractResourceDetails(string? resourceUri)
        {
            if (string.IsNullOrWhiteSpace(resourceUri))
            {
                return ("unknown", "unknown");
            }

            try
            {
                // Example: "Users/john@contoso.com/Messages/AAMkAGI2..."  
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

                return (userId, messageId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Failed to parse resource URI: {ResourceUri}", resourceUri);
                return ("unknown", "unknown");
            }
        }

        private async Task<bool> ValidateClientStateAsync(
            GraphNotification.ChangeNotification notification,
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
                    SELECT client_state   
                    FROM mail_subscriptions   
                    WHERE subscription_id = @SubscriptionId   
                    AND subscription_expiration_time > NOW();";

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
                _logger.LogError(ex, "❌ Error validating client state for SubscriptionId: {SubscriptionId}",
                    notification.SubscriptionId);
                return false;
            }
        }

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