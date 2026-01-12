using System;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Message payload sent to RabbitMQ for email notification processing.  
    /// </summary>  
    public class NotificationQueueMessage
    {
        /// <summary>  
        /// Unique notification identifier from the database.  
        /// </summary>  
        public string NotificationId { get; set; } = string.Empty;

        /// <summary>  
        /// User identifier who owns the mailbox.  
        /// </summary>  
        public string UserId { get; set; } = string.Empty;

        /// <summary>  
        /// Email message identifier from Microsoft Graph.  
        /// </summary>  
        public string MessageId { get; set; } = string.Empty;

        /// <summary>  
        /// Timestamp when the message was queued.  
        /// </summary>  
        public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

        /// <summary>  
        /// Change type (created, updated, deleted).  
        /// </summary>  
        public string ChangeType { get; set; } = string.Empty;

        /// <summary>  
        /// Subscription identifier.  
        /// </summary>  
        public string SubscriptionId { get; set; } = string.Empty;
    }
}