using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Performance statistics.  
    /// </summary>  
    public class PerformanceStats
    {
        /// <summary>Number of successfully completed notifications.</summary>  
        public int SuccessCount { get; set; }

        /// <summary>Number of failed notifications.</summary>  
        public int FailedCount { get; set; }

        /// <summary>Number of pending notifications.</summary>  
        public int PendingCount { get; set; }

        /// <summary>Number of notifications currently being processed.</summary>  
        public int ProcessingCount { get; set; }
    }
}
