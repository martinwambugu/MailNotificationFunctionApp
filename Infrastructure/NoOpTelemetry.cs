using MailNotificationFunctionApp.Interfaces;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Infrastructure
{
    public class NoOpTelemetry : ICustomTelemetry
    {
        private readonly ILogger<NoOpTelemetry> _logger;

        public NoOpTelemetry(ILogger<NoOpTelemetry> logger)
        {
            _logger = logger;
        }

        public void TrackEvent(string eventName, IDictionary<string, string>? properties = null)
        {
            // Just log locally for now — could be extended to send to OTLP  
            _logger.LogInformation("Telemetry event: {EventName} {@Properties}", eventName, properties);
        }

        public void TrackException(Exception exception, IDictionary<string, string>? properties = null)
        {
            _logger.LogError(exception, "Telemetry Exception {@Properties}", properties);
        }

        public void TrackTrace(string message, SeverityLevel severityLevel, IDictionary<string, string>? properties = null)
        {
            // Map Application Insights SeverityLevel to ILogger log level  
            var logLevel = severityLevel switch
            {
                SeverityLevel.Critical => LogLevel.Critical,
                SeverityLevel.Error => LogLevel.Error,
                SeverityLevel.Warning => LogLevel.Warning,
                SeverityLevel.Information => LogLevel.Information,
                SeverityLevel.Verbose => LogLevel.Debug,
                _ => LogLevel.Information
            };

            _logger.Log(logLevel, "Telemetry Trace: {Message} {@Properties}", message, properties);
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            // Log metric information instead of sending telemetry  
            if (properties != null)
            {
                _logger.LogInformation("Telemetry Metric: {Name} = {Value} {@Properties}", name, value, properties);
            }
            else
            {
                _logger.LogInformation("Telemetry Metric: {Name} = {Value}", name, value);
            }
        }

        /// <summary>  
        /// Tracks a dependency (e.g., Business Central API, SQL query).  
        /// </summary>  
        public void TrackDependency(string dependencyType, string dependencyName, DateTime startTime, TimeSpan duration, bool success)
        {
            // No Application Insights TelemetryClient here—just log locally  
            _logger.LogInformation(
                "Telemetry Dependency: {Type} - {Name} | Success: {Success} | Duration: {Duration}ms | Timestamp: {Timestamp}",
                dependencyType,
                dependencyName,
                success,
                duration.TotalMilliseconds,
                startTime);
        }
    }
}
