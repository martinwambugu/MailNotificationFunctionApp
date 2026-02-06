using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Count of notifications by status.  
    /// </summary>  
    public class StatusCount
    {
        /// <summary>Processing status (Pending, Processing, Completed, Failed).</summary>  
        public string Status { get; set; } = string.Empty;

        /// <summary>Number of notifications with this status.</summary>  
        public int Count { get; set; }
    }
}
