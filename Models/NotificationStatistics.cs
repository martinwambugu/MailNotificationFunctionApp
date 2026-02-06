using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Comprehensive notification statistics.  
    /// </summary>  
    public class NotificationStatistics
    {
        /// <summary>Human-readable time period description.</summary>  
        public string Period { get; set; } = string.Empty;

        /// <summary>Time window in hours.</summary>  
        public int TimeWindowHours { get; set; }

        /// <summary>Total number of notifications in the time window.</summary>  
        public int TotalNotifications { get; set; }

        /// <summary>Breakdown of notifications by processing status.</summary>  
        public List<StatusCount> ByStatus { get; set; } = new();

        /// <summary>Overall success rate percentage (0-100).</summary>  
        public double SuccessRate { get; set; }

        /// <summary>Average number of retries per notification.</summary>  
        public double AverageRetryCount { get; set; }

        /// <summary>Maximum retry count observed.</summary>  
        public int MaxRetryCount { get; set; }

        /// <summary>Number of notifications that required retries.</summary>  
        public int NotificationsWithRetries { get; set; }

        /// <summary>Number of notifications currently pending.</summary>  
        public int PendingCount { get; set; }

        /// <summary>Number of notifications currently being processed.</summary>  
        public int ProcessingCount { get; set; }

        /// <summary>Number of successfully completed notifications.</summary>  
        public int CompletedCount { get; set; }

        /// <summary>Number of failed notifications.</summary>  
        public int FailedCount { get; set; }

        /// <summary>Hourly activity breakdown.</summary>  
        public List<HourlyActivity> RecentActivity { get; set; } = new();

        /// <summary>Timestamp when statistics were generated.</summary>  
        public DateTime Timestamp { get; set; }

        /// <summary>Correlation ID for tracking.</summary>  
        public string CorrelationId { get; set; } = string.Empty;
    }
}
