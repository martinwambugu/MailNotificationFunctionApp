using Microsoft.ApplicationInsights.DataContracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
    public interface ICustomTelemetry
    {
        /// <summary>  
        /// Track a custom event with optional properties.  
        /// </summary>  
        void TrackEvent(string eventName, IDictionary<string, string>? properties = null);

        /// <summary>  
        /// Track an exception with optional custom properties.  
        /// </summary>  
        void TrackException(Exception exception, IDictionary<string, string>? properties = null);

        /// <summary>  
        /// Track a trace message with severity and optional properties.  
        /// </summary>  
        void TrackTrace(string message, SeverityLevel severityLevel, IDictionary<string, string>? properties = null);
        void TrackMetric(string name, double value, IDictionary<string, string>? properties = null);
        /// <summary>  
        /// Track a dependency call (e.g., external API, database).  
        /// </summary>  
        /// <param name="dependencyType">Type of dependency (e.g., "BusinessCentral", "SQL").</param>  
        /// <param name="dependencyName">Name or target of the dependency.</param>  
        /// <param name="startTime">Start time of the dependency call.</param>  
        /// <param name="duration">Duration of the dependency call.</param>  
        /// <param name="success">Indicates whether the dependency call succeeded.</param>  
        void TrackDependency(string dependencyType, string dependencyName, DateTime startTime, TimeSpan duration, bool success);
    }
}
