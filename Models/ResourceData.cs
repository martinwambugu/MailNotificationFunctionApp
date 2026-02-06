using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Represents detailed information about the changed resource in a notification.  
    /// </summary>  
    /// <remarks>  
    /// Contains metadata about the resource that changed, such as the message ID,  
    /// resource type, and ETag for change tracking.  
    /// </remarks>  
    public class ResourceData
    {
        /// <summary>  
        /// Unique identifier of the resource (e.g., message ID, event ID).  
        /// </summary>  
        /// <remarks>  
        /// This is the actual Graph API resource ID that can be used to fetch  
        /// the full resource details via Microsoft Graph API.  
        /// </remarks>  
        public string? Id { get; set; }

        /// <summary>  
        /// OData type of the resource (e.g., "#Microsoft.Graph.Message").  
        /// </summary>  
        public string? ODataType { get; set; }

        /// <summary>  
        /// ETag value for change tracking and concurrency control.  
        /// </summary>  
        public string? ODataEtag { get; set; }
    }
}
