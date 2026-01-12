using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Services
{
    /// <summary>  
    /// Handles retry logic for failed email notifications.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This service retrieves notifications with status 'Failed' and retry_count less than 10,  
    /// then attempts to reprocess them. It is designed to be called by a timer-triggered function.  
    /// </para>  
    /// <para>  
    /// <b>Retry Policy:</b>  
    /// <list type="bullet">  
    ///     <item>Maximum retry count: 10 attempts</item>  
    ///     <item>Time window: Last 24 hours only</item>  
    ///     <item>Batch size: 100 notifications per execution</item>  
    /// </list>  
    /// </para>  
    /// </remarks>  
    public class NotificationRetryService
    {
        private readonly IDbConnectionFactory _dbFactory;
        private readonly INotificationHandler _handler;
        private readonly ILogger<NotificationRetryService> _logger;
        private readonly ICustomTelemetry _telemetry;

        /// <summary>  
        /// Initializes a new instance of the <see cref="NotificationRetryService"/> class.  
        /// </summary>  
        public NotificationRetryService(
            IDbConnectionFactory dbFactory,
            INotificationHandler handler,
            ILogger<NotificationRetryService> logger,
            ICustomTelemetry telemetry)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        /// <summary>  
        /// Retrieves and retries failed notifications that haven't exceeded max retry count.  
        /// </summary>  
        /// <param name="cancellationToken">Cancellation token to cancel the retry operation.</param>  
        /// <returns>The number of notifications successfully retried.</returns>  
        /// <remarks>  
        /// <para>  
        /// This method processes up to 100 failed notifications per execution.  
        /// Notifications are ordered by received_date_time (oldest first) to ensure FIFO processing.  
        /// </para>  
        /// <para>  
        /// <b>Performance:</b> Uses batch processing to avoid overloading the database and downstream services.  
        /// </para>  
        /// </remarks>  
        public async Task<int> RetryFailedNotificationsAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"  
                SELECT   
                    notification_id,  
                    subscription_id,  
                    change_type,  
                    resource_uri,  
                    resource_id,  
                    notification_date_time,  
                    received_date_time,  
                    raw_notification_payload,  
                    processing_status,  
                    error_message,  
                    retry_count  
                FROM email_notifications  
                WHERE processing_status = 'Failed'  
                AND retry_count < 10  
                AND received_date_time > NOW() - INTERVAL '24 hours'  
                ORDER BY received_date_time ASC  
                LIMIT 100;";

            try
            {
                // ✅ Use 'using' instead of 'await using'  
                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                var failedNotifications = await conn.QueryAsync<EmailNotification>(
                    new CommandDefinition(sql, cancellationToken: cancellationToken));

                var retriedCount = 0;
                var failedRetryCount = 0;

                foreach (var notification in failedNotifications)
                {
                    try
                    {
                        _logger.LogInformation(
                            "🔄 Retrying notification {NotificationId} (attempt {RetryCount})",
                            notification.NotificationId, notification.RetryCount + 1);

                        // Deserialize original payload  
                        var changeNotification = JsonSerializer.Deserialize<GraphNotification.ChangeNotification>(
                            notification.RawNotificationPayload ?? "{}",
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (changeNotification != null)
                        {
                            var success = await _handler.HandleAsync(
                                changeNotification,
                                notification.RawNotificationPayload!,
                                cancellationToken);

                            if (success)
                            {
                                retriedCount++;
                                _logger.LogInformation(
                                    "✅ Notification {NotificationId} retried successfully",
                                    notification.NotificationId);
                            }
                            else
                            {
                                failedRetryCount++;
                                _logger.LogWarning(
                                    "⚠️ Notification {NotificationId} retry returned false",
                                    notification.NotificationId);
                            }
                        }
                        else
                        {
                            _logger.LogWarning(
                                "⚠️ Failed to deserialize notification {NotificationId}",
                                notification.NotificationId);
                            failedRetryCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Retry failed for notification {NotificationId}",
                            notification.NotificationId);

                        _telemetry.TrackException(ex, new Dictionary<string, string>
                        {
                            { "NotificationId", notification.NotificationId },
                            { "RetryCount", notification.RetryCount.ToString() },
                            { "Operation", "RetryFailedNotificationsAsync" }
                        });

                        failedRetryCount++;
                    }
                }

                _logger.LogInformation(
                    "✅ Retry process completed. Succeeded: {Succeeded}, Failed: {Failed}, Total: {Total}",
                    retriedCount, failedRetryCount, failedNotifications.Count());

                _telemetry.TrackMetric("MailNotifications_Retried_Success", retriedCount);
                _telemetry.TrackMetric("MailNotifications_Retried_Failed", failedRetryCount);

                return retriedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during retry process");
                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "RetryFailedNotificationsAsync" }
                });
                throw;
            }
        }
    }
}