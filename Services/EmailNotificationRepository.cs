using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
using Dapper;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Services
{
    /// <summary>  
    /// Implements PostgreSQL persistence for <see cref="EmailNotification"/> using Dapper.  
    /// </summary>  
    public class EmailNotificationRepository : IEmailNotificationRepository
    {
        private readonly IDbConnectionFactory _dbFactory;
        private readonly ILogger<EmailNotificationRepository> _logger;
        private readonly ICustomTelemetry _telemetry;

        public EmailNotificationRepository(IDbConnectionFactory dbFactory, ILogger<EmailNotificationRepository> logger, ICustomTelemetry telemetry)
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _telemetry = telemetry;
        }

        /// <inheritdoc/>  
        public async Task SaveNotificationAsync(EmailNotification notification)
        {
            const string sql = @"  
                INSERT INTO ""EmailNotifications"" (  
                    ""NotificationId"",  
                    ""SubscriptionId"",  
                    ""ChangeType"",  
                    ""ResourceUri"",  
                    ""ResourceId"",  
                    ""NotificationDateTime"",  
                    ""ReceivedDateTime"",  
                    ""RawNotificationPayload"",  
                    ""ProcessingStatus"",  
                    ""ErrorMessage"",  
                    ""RetryCount""  
                ) VALUES (  
                    @NotificationId,  
                    @SubscriptionId,  
                    @ChangeType,  
                    @ResourceUri,  
                    @ResourceId,  
                    @NotificationDateTime,  
                    @ReceivedDateTime,  
                    @RawNotificationPayload,  
                    @ProcessingStatus,  
                    @ErrorMessage,  
                    @RetryCount  
                );";

            try
            {
                using var conn = _dbFactory.CreateConnection();
                await ((dynamic)conn).OpenAsync();
                await conn.ExecuteAsync(sql, notification);

                _logger.LogInformation("✅ Email notification saved: {NotificationId}", notification.NotificationId);
                _telemetry.TrackEvent("EmailNotification_Saved", new Dictionary<string, string>
                {
                    { "NotificationId", notification.NotificationId },
                    { "SubscriptionId", notification.SubscriptionId },
                    { "ChangeType", notification.ChangeType }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to save EmailNotification: {NotificationId}", notification.NotificationId);
                _telemetry.TrackException(ex, new Dictionary<string, string> { { "NotificationId", notification.NotificationId } });
                throw;
            }
        }
    }
}