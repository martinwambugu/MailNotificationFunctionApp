using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Hourly activity breakdown.  
    /// </summary>  
    public class HourlyActivity
    {
        /// <summary>Hour timestamp (truncated to hour).</summary>  
        public DateTime Hour { get; set; }

        /// <summary>Number of notifications in this hour.</summary>  
        public int Count { get; set; }

        /// <summary>Processing status.</summary>  
        public string Status { get; set; } = string.Empty;
    }
}
