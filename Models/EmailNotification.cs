using System;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Represents a persisted email notification record stored in PostgreSQL table <c>EmailNotifications</c>.  
    /// </summary>  
    public class EmailNotification
    {
        public string NotificationId { get; set; } = default!;
        public string SubscriptionId { get; set; } = default!;
        public string ChangeType { get; set; } = default!;
        public string? ResourceUri { get; set; }
        public string? ResourceId { get; set; }
        public DateTime NotificationDateTime { get; set; }
        public DateTime ReceivedDateTime { get; set; } = DateTime.UtcNow;
        public string? RawNotificationPayload { get; set; }
        public string ProcessingStatus { get; set; } = "Pending";
        public string? ErrorMessage { get; set; }
        public int RetryCount { get; set; } = 0;
    }
}