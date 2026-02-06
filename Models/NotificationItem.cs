using System;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Represents a single notification item from Microsoft Graph webhook.  
    /// This is the normalized structure passed to the notification handler.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This class bridges the gap between the raw <see cref="GraphNotification.ChangeNotification"/>  
    /// and the handler's processing logic. It provides a cleaner, more strongly-typed interface.  
    /// </para>  
    /// <para>  
    /// <b>Use Cases:</b>  
    /// - Initial webhook processing (converted from GraphNotification.ChangeNotification)  
    /// - Retry operations (reconstructed from EmailNotification database record)  
    /// </para>  
    /// </remarks>  
    public class NotificationItem
    {
        /// <summary>  
        /// Unique identifier for this notification (from Microsoft Graph).  
        /// </summary>  
        public string? Id { get; set; }

        /// <summary>  
        /// Subscription ID that triggered this notification.  
        /// </summary>  
        /// <remarks>  
        /// Used to validate client state and determine which mailbox to process.  
        /// </remarks>  
        public string SubscriptionId { get; set; } = string.Empty;

        /// <summary>  
        /// Type of change that occurred (e.g., "created", "updated", "deleted").  
        /// </summary>  
        public string ChangeType { get; set; } = string.Empty;

        /// <summary>  
        /// Resource URI pointing to the changed item (e.g., "Users/{userId}/Messages/{messageId}").  
        /// </summary>  
        public string? Resource { get; set; }

        /// <summary>  
        /// Client state value for validation (must match subscription's client state).  
        /// </summary>  
        /// <remarks>  
        /// <para>  
        /// <b>Security:</b> This value MUST be validated against the stored subscription's  
        /// client state to prevent spoofing attacks. If null during retry, it will be  
        /// skipped (client state validation already passed during initial processing).  
        /// </para>  
        /// </remarks>  
        public string? ClientState { get; set; }

        /// <summary>  
        /// Tenant ID (Azure AD tenant identifier).  
        /// </summary>  
        public string? TenantId { get; set; }

        /// <summary>  
        /// Subscription expiration date/time (ISO 8601 format).  
        /// </summary>  
        /// <remarks>  
        /// Used to determine if subscription needs renewal.  
        /// </remarks>  
        public DateTime? SubscriptionExpirationDateTime { get; set; }

        /// <summary>  
        /// Detailed information about the changed resource.  
        /// </summary>  
        public ResourceData? ResourceData { get; set; }

        /// <summary>  
        /// Encrypted content data (for rich notifications with change data).  
        /// </summary>  
        /// <remarks>  
        /// Only present when subscription is configured with includeResourceData=true.  
        /// Requires decryption using subscription's encryption certificate.  
        /// </remarks>  
        public string? EncryptedContentData { get; set; }
    }

    
}