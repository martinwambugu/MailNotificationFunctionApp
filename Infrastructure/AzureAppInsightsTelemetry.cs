using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using MailNotificationFunctionApp.Interfaces;
using System;
using System.Collections.Generic;

namespace MailNotificationFunctionApp.Infrastructure
{
    /// <summary>  
    /// Application Insights telemetry implementation for tracking events, exceptions, traces, metrics, and dependencies.  
    /// </summary>  
    public class AzureAppInsightsTelemetry : ICustomTelemetry
    {
        private readonly TelemetryClient _telemetryClient;

        /// <summary>  
        /// Initializes a new instance of the <see cref="AzureAppInsightsTelemetry"/> class.  
        /// </summary>  
        /// <param name="telemetryClient">Application Insights telemetry client.</param>  
        public AzureAppInsightsTelemetry(TelemetryClient telemetryClient)
        {
            _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
        }

        /// <inheritdoc/>  
        public void TrackEvent(string eventName, IDictionary<string, string>? properties = null)
        {
            _telemetryClient.TrackEvent(eventName, properties);
        }

        /// <inheritdoc/>  
        public void TrackException(Exception exception, IDictionary<string, string>? properties = null)
        {
            _telemetryClient.TrackException(exception, properties);
        }

        /// <inheritdoc/>  
        public void TrackTrace(string message, SeverityLevel severityLevel, IDictionary<string, string>? properties = null)
        {
            _telemetryClient.TrackTrace(message, severityLevel, properties);
        }

        /// <inheritdoc/>  
        public void TrackMetric(string name, double value, IDictionary<string, string>? properties = null)
        {
            if (properties != null)
            {
                var metricTelemetry = new MetricTelemetry(name, value);
                foreach (var kvp in properties)
                {
                    metricTelemetry.Properties[kvp.Key] = kvp.Value;
                }
                _telemetryClient.TrackMetric(metricTelemetry);
            }
            else
            {
                _telemetryClient.GetMetric(name).TrackValue(value);
            }
        }

        /// <inheritdoc/>  
        public void TrackDependency(string dependencyType, string dependencyName, DateTime startTime, TimeSpan duration, bool success)
        {
            var dependencyTelemetry = new DependencyTelemetry
            {
                Type = dependencyType,
                Name = dependencyName,
                Timestamp = startTime,
                Duration = duration,
                Success = success
            };

            _telemetryClient.TrackDependency(dependencyTelemetry);
        }
    }
}