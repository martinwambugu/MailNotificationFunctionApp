using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Services
{
    /// <summary>  
    /// Handles processing and persistence of mail notifications.  
    /// </summary>  
    public class NotificationHandler : INotificationHandler
    {
        private readonly IEmailNotificationRepository _repo;
        private readonly ILogger<NotificationHandler> _logger;
        private readonly ICustomTelemetry _telemetry;

        public NotificationHandler(IEmailNotificationRepository repo, ILogger<NotificationHandler> logger, ICustomTelemetry telemetry)
        {
            _repo = repo;
            _logger = logger;
            _telemetry = telemetry;
        }

        /// <inheritdoc/>  
        public async Task HandleAsync(GraphNotification.ChangeNotification notification, string rawJson)
        {
            var entity = new EmailNotification
            {
                NotificationId = Guid.NewGuid().ToString(),
                SubscriptionId = notification.SubscriptionId ?? "unknown",
                ChangeType = notification.ChangeType ?? "created",
                ResourceUri = notification.Resource ?? string.Empty,
                ResourceId = notification.ResourceDataId ?? Guid.NewGuid().ToString(),
                NotificationDateTime = DateTime.UtcNow,
                RawNotificationPayload = rawJson
            };

            _logger.LogInformation("📬 Handling notification: SubscriptionId={SubscriptionId}, ResourceId={ResourceId}", entity.SubscriptionId, entity.ResourceId);

            _telemetry.TrackEvent("MailNotification_Received", new Dictionary<string, string>
            {
                { "SubscriptionId", entity.SubscriptionId },
                { "ResourceId", entity.ResourceId },
                { "ChangeType", entity.ChangeType }
            });

            try
            {
                await _repo.SaveNotificationAsync(entity);
                _telemetry.TrackEvent("MailNotification_Saved", new Dictionary<string, string> { { "NotificationId", entity.NotificationId } });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving notification {NotificationId}", entity.NotificationId);
                _telemetry.TrackException(ex);
                throw;
            }
        }
    }
}