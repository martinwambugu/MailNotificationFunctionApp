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
    /// <summary>  
    /// Handles processing and persistence of mail notifications with security validation.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This service validates incoming Microsoft Graph notifications by checking the client state  
    /// against the stored value in the database, preventing webhook spoofing attacks.  
    /// </para>  
    /// <para>  
    /// <b>Security:</b> Client state validation is CRITICAL for webhook security.  
    /// </para>  
    /// </remarks>  
    public class NotificationHandler : INotificationHandler
    {
        private readonly IEmailNotificationRepository _repo;
        private readonly IDbConnectionFactory _dbFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<NotificationHandler> _logger;
        private readonly ICustomTelemetry _telemetry;

        /// <summary>  
        /// Initializes a new instance of the <see cref="NotificationHandler"/> class.  
        /// </summary>  
        public NotificationHandler(
            IEmailNotificationRepository repo,
            IDbConnectionFactory dbFactory,
            IConfiguration config,
            ILogger<NotificationHandler> logger,
            ICustomTelemetry telemetry)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        /// <inheritdoc/>  
        public async Task<bool> HandleAsync(
            GraphNotification.ChangeNotification notification,
            string rawJson,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(notification);

            _logger.LogInformation(
                "📬 Handling notification: SubscriptionId={SubscriptionId}, ChangeType={ChangeType}",
                notification.SubscriptionId, notification.ChangeType);

            // ✅ CRITICAL: Validate client state to prevent spoofing  
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
                // ✅ Pass cancellationToken to SaveNotificationAsync  
                await _repo.SaveNotificationAsync(entity, cancellationToken);

                _telemetry.TrackEvent("MailNotification_Saved", new Dictionary<string, string>
                {
                    { "NotificationId", entity.NotificationId }
                });

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

                // ✅ Try to save the failed notification for retry processing  
                try
                {
                    // ✅ Pass cancellationToken here too  
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
        /// Validates the client state from the notification against the stored value in the database.  
        /// </summary>  
        /// <remarks>  
        /// <para>  
        /// This is a critical security check to prevent webhook spoofing attacks.  
        /// Microsoft Graph includes the client state value you provided when creating the subscription.  
        /// </para>  
        /// <para>  
        /// <b>Timing Attack Prevention:</b> Uses constant-time string comparison to prevent timing attacks.  
        /// </para>  
        /// </remarks>  
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
                // ✅ Retrieve the expected client state from the database  
                const string sql = @"  
                    SELECT client_state   
                    FROM mail_subscriptions   
                    WHERE subscription_id = @SubscriptionId   
                    AND subscription_expiration_time > NOW();";

                // ✅ Use 'using' instead of 'await using'  
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

                // ✅ Constant-time comparison to prevent timing attacks  
                var isValid = CryptographicEquals(notification.ClientState, expectedClientState);

                if (!isValid)
                {
                    _logger.LogWarning(
                        "🚨 Client state mismatch for SubscriptionId: {SubscriptionId}. " +
                        "Expected: {Expected}, Received: {Received}",
                        notification.SubscriptionId,
                        expectedClientState,
                        notification.ClientState);
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

        /// <summary>  
        /// Performs constant-time string comparison to prevent timing attacks.  
        /// </summary>  
        /// <remarks>  
        /// This method compares two strings in constant time regardless of where differences occur,  
        /// preventing attackers from using timing analysis to guess the expected value.  
        /// </remarks>  
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