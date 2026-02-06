using MailNotificationFunctionApp.Models;
using System;

namespace MailNotificationFunctionApp.Extensions
{
    /// <summary>  
    /// Extension methods for converting between notification types.  
    /// </summary>  
    public static class NotificationExtensions
    {
        /// <summary>  
        /// Converts a <see cref="GraphNotification.ChangeNotification"/> to a <see cref="NotificationItem"/>.  
        /// </summary>  
        /// <param name="changeNotification">The raw Graph notification from webhook.</param>  
        /// <returns>A normalized <see cref="NotificationItem"/> for handler processing.</returns>  
        /// <remarks>  
        /// <para>  
        /// <b>Use Case:</b> Initial webhook processing when notification is first received.  
        /// </para>  
        /// </remarks>  
        public static NotificationItem ToNotificationItem(this GraphNotification.ChangeNotification changeNotification)
        {
            ArgumentNullException.ThrowIfNull(changeNotification);

            return new NotificationItem
            {
                Id = changeNotification.Id,
                SubscriptionId = changeNotification.SubscriptionId ?? string.Empty,
                ChangeType = changeNotification.ChangeType ?? string.Empty,
                Resource = changeNotification.Resource,
                ClientState = changeNotification.ClientState,
                TenantId = changeNotification.TenantId,
                SubscriptionExpirationDateTime = ParseDateTime(changeNotification.SubscriptionExpirationDateTime),
                ResourceData = new ResourceData
                {
                    Id = changeNotification.ResourceDataId,
                    ODataType = changeNotification.ResourceDataType,
                    ODataEtag = changeNotification.ResourceDataEtag
                },
                EncryptedContentData = changeNotification.EncryptedContentData
            };
        }

        /// <summary>  
        /// Reconstructs a <see cref="NotificationItem"/> from a stored <see cref="EmailNotification"/>.  
        /// </summary>  
        /// <param name="emailNotification">The persisted notification record from database.</param>  
        /// <returns>A reconstructed <see cref="NotificationItem"/> for retry processing.</returns>  
        /// <remarks>  
        /// <para>  
        /// <b>Use Case:</b> Retry operations when reprocessing failed notifications.  
        /// </para>  
        /// <para>  
        /// <b>Note:</b> ClientState is set to null because client state validation  
        /// already passed during initial processing. The handler should skip  
        /// client state validation for retry operations.  
        /// </para>  
        /// </remarks>  
        public static NotificationItem ToNotificationItem(this EmailNotification emailNotification)
        {
            ArgumentNullException.ThrowIfNull(emailNotification);

            return new NotificationItem
            {
                Id = emailNotification.NotificationId,
                SubscriptionId = emailNotification.SubscriptionId,
                ChangeType = emailNotification.ChangeType,
                Resource = emailNotification.ResourceUri,
                ClientState = null, // Skip client state validation for retries  
                TenantId = null, // Not stored in database  
                SubscriptionExpirationDateTime = emailNotification.NotificationDateTime,
                ResourceData = new ResourceData
                {
                    Id = emailNotification.ResourceId,
                    ODataType = null, // Not stored in database  
                    ODataEtag = null  // Not stored in database  
                },
                EncryptedContentData = null // Not stored in database  
            };
        }

        /// <summary>  
        /// Parses an ISO 8601 date string to nullable DateTime.  
        /// </summary>  
        private static DateTime? ParseDateTime(string? dateTimeString)
        {
            if (string.IsNullOrWhiteSpace(dateTimeString))
                return null;

            if (DateTime.TryParse(dateTimeString, out var result))
                return result;

            return null;
        }
    }
}