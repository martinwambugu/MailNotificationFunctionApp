using Dapper;
using MailNotificationFunctionApp.Infrastructure;
using MailNotificationFunctionApp.Interfaces;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Functions.Health
{
    /// <summary>  
    /// Health check endpoint for monitoring and diagnostics.  
    /// </summary>  
    public class HealthCheckFunction : BaseFunction
    {
        private readonly IDbConnectionFactory _dbFactory;
        private readonly IMessageQueuePublisher _queuePublisher;

        public HealthCheckFunction(
            IDbConnectionFactory dbFactory,
            IMessageQueuePublisher queuePublisher,
            ILogger<HealthCheckFunction> logger,
            ICustomTelemetry telemetry)
            : base(logger, telemetry)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _queuePublisher = queuePublisher ?? throw new ArgumentNullException(nameof(queuePublisher));
        }

        [Function("HealthCheck")]
        [OpenApiOperation(
            operationId: "HealthCheck",
            tags: new[] { "Health" },
            Summary = "Check application health status.",
            Description = "Returns health status of database and message queue connections.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "application/json",
            typeof(HealthCheckResponse),
            Description = "Health check successful.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.ServiceUnavailable,
            "application/json",
            typeof(HealthCheckResponse),
            Description = "One or more dependencies are unhealthy.")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")]
            HttpRequestData req,
            CancellationToken cancellationToken)
        {
            LogStart(nameof(HealthCheckFunction));

            var health = new HealthCheckResponse
            {
                Status = "Healthy",
                Timestamp = DateTime.UtcNow,
                Checks = new Dictionary<string, ComponentHealth>()
            };

            // Check database connection  
            try
            {
                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);
                await conn.ExecuteAsync("SELECT 1");

                health.Checks["Database"] = new ComponentHealth
                {
                    Status = "Healthy",
                    ResponseTime = "< 100ms"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Database health check failed");
                health.Checks["Database"] = new ComponentHealth
                {
                    Status = "Unhealthy",
                    Error = ex.Message
                };
                health.Status = "Unhealthy";
            }

            // Check RabbitMQ connection  
            try
            {
                var queueHealthy = await _queuePublisher.IsHealthyAsync(cancellationToken);

                health.Checks["MessageQueue"] = new ComponentHealth
                {
                    Status = queueHealthy ? "Healthy" : "Unhealthy"
                };

                if (!queueHealthy)
                    health.Status = "Degraded";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Message queue health check failed");
                health.Checks["MessageQueue"] = new ComponentHealth
                {
                    Status = "Unhealthy",
                    Error = ex.Message
                };
                health.Status = "Unhealthy";
            }

            var statusCode = health.Status == "Healthy"
                ? HttpStatusCode.OK
                : HttpStatusCode.ServiceUnavailable;

            var response = req.CreateResponse(statusCode);
            await response.WriteAsJsonAsync(health, cancellationToken: cancellationToken);

            LogEnd(nameof(HealthCheckFunction), health);
            return response;
        }
    }

    public class HealthCheckResponse
    {
        public string Status { get; set; } = "Unknown";
        public DateTime Timestamp { get; set; }
        public Dictionary<string, ComponentHealth> Checks { get; set; } = new();
    }

    public class ComponentHealth
    {
        public string Status { get; set; } = "Unknown";
        public string? ResponseTime { get; set; }
        public string? Error { get; set; }
    }
}