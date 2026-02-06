using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Retry statistics.  
    /// </summary>  
    public class RetryStats
    {
        /// <summary>Average retry count across all notifications.</summary>  
        public double? AverageRetryCount { get; set; }

        /// <summary>Maximum retry count observed.</summary>  
        public int? MaxRetryCount { get; set; }

        /// <summary>Number of notifications that required retries.</summary>  
        public int? NotificationsWithRetries { get; set; }
    }
}
