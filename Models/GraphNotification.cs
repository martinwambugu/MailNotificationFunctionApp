using System.Collections.Generic;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Represents the Microsoft Graph webhook payload structure.  
    /// </summary>  
    public class GraphNotification
    {
        public List<ChangeNotification> Value { get; set; } = new();

        public List<string>? ValidationTokens { get; set; }

        public class ChangeNotification
        {
            public string? Id { get; set; }
            public string? SubscriptionId { get; set; }
            public string? SubscriptionExpirationDateTime { get; set; }
            public string? ChangeType { get; set; }
            public string? Resource { get; set; }
            public string? ClientState { get; set; }
            public string? TenantId { get; set; }
            public string? ResourceDataId { get; set; }
            public string? ResourceDataType { get; set; }
            public string? ResourceDataEtag { get; set; }
            public string? EncryptedContentData { get; set; }
        }
    }
}