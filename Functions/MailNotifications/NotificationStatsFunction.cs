using MailNotificationFunctionApp.Infrastructure;
using MailNotificationFunctionApp.Interfaces;
using Dapper;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MailNotificationFunctionApp.Models;

namespace MailNotificationFunctionApp.Functions.MailNotifications
{
    /// <summary>  
    /// Provides comprehensive statistics about notification processing.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// <b>Security:</b> Requires API key authentication to prevent unauthorized access to metrics.  
    /// </para>  
    /// <para>  
    /// <b>Performance:</b> Uses efficient aggregate queries with indexes on receiveddatetime and processingstatus.  
    /// </para>  
    /// <para>  
    /// <b>Caching:</b> Results can be cached for 1-5 minutes to reduce database load.  
    /// </para>  
    /// </remarks>  
    public class NotificationStatsFunction : BaseFunction
    {
        private readonly IDbConnectionFactory _dbFactory;

        public NotificationStatsFunction(
            IDbConnectionFactory dbFactory,
            ILogger<NotificationStatsFunction> logger,
            ICustomTelemetry telemetry)
            : base(logger, telemetry)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        }

        /// <summary>  
        /// Get comprehensive notification processing statistics.  
        /// </summary>  
        /// <param name="req">HTTP request.</param>  
        /// <param name="cancellationToken">Cancellation token.</param>  
        /// <returns>Statistics including counts by status, retry metrics, and performance indicators.</returns>  
        [Function("NotificationStats")]
        [OpenApiOperation(
            operationId: "NotificationStats",
            tags: new[] { "Notifications" },
            Summary = "Get notification processing statistics.",
            Description = "Returns counts of notifications by status, retry metrics, and performance indicators for the specified time period.")]
        [OpenApiSecurity(
            "ApiKeyAuth",
            SecuritySchemeType.ApiKey,
            Name = "x-api-key",
            In = OpenApiSecurityLocationType.Header)]
        [OpenApiParameter(
            name: "hours",
            In = ParameterLocation.Query,
            Required = false,
            Type = typeof(int),
            Description = "Time window in hours (default: 24, max: 168 for 7 days).")]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "application/json",
            typeof(NotificationStatistics),
            Description = "Statistics retrieved successfully.")]
        [OpenApiResponseWithoutBody(
            HttpStatusCode.BadRequest,
            Description = "Invalid time window parameter.")]
        [OpenApiResponseWithoutBody(
            HttpStatusCode.InternalServerError,
            Description = "Error retrieving statistics.")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "notifications/stats")]
            HttpRequestData req,
            CancellationToken cancellationToken)
        {
            var correlationId = Guid.NewGuid().ToString("N");

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["FunctionName"] = nameof(NotificationStatsFunction)
            }))
            {
                LogStart(nameof(NotificationStatsFunction), new { correlationId });

                try
                {
                    // ✅ Parse and validate time window parameter  
                    var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                    var hours = 24;

                    if (queryParams["hours"] != null)
                    {
                        if (!int.TryParse(queryParams["hours"], out hours) || hours < 1 || hours > 168)
                        {
                            _logger.LogWarning(
                                "⚠️ Invalid hours parameter: {Value}. Must be between 1 and 168. CorrelationId: {CorrelationId}",
                                queryParams["hours"],
                                correlationId);

                            return await BadRequest(req, "Invalid hours parameter. Must be between 1 and 168.");
                        }
                    }

                    _logger.LogInformation(
                        "📊 Retrieving statistics for last {Hours} hours. CorrelationId: {CorrelationId}",
                        hours,
                        correlationId);

                    using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                    // ✅ FIXED: Parameterized query to prevent SQL injection  
                    const string statusCountsSql = @"  
                        SELECT   
                            processingstatus AS Status,  
                            COUNT(*) AS Count  
                        FROM emailnotifications  
                        WHERE receiveddatetime > NOW() - INTERVAL '1 hour' * @Hours  
                        GROUP BY processingstatus  
                        ORDER BY processingstatus;";

                    const string totalSql = @"  
                        SELECT COUNT(*)   
                        FROM emailnotifications  
                        WHERE receiveddatetime > NOW() - INTERVAL '1 hour' * @Hours;";

                    // ✅ Additional metrics for comprehensive statistics  
                    const string retryStatsSql = @"  
                        SELECT   
                            AVG(retrycount) AS AverageRetryCount,  
                            MAX(retrycount) AS MaxRetryCount,  
                            COUNT(*) FILTER (WHERE retrycount > 0) AS NotificationsWithRetries  
                        FROM emailnotifications  
                        WHERE receiveddatetime > NOW() - INTERVAL '1 hour' * @Hours;";

                    const string performanceStatsSql = @"  
                        SELECT   
                            COUNT(*) FILTER (WHERE processingstatus = 'Completed') AS SuccessCount,  
                            COUNT(*) FILTER (WHERE processingstatus = 'Failed') AS FailedCount,  
                            COUNT(*) FILTER (WHERE processingstatus = 'Pending') AS PendingCount,  
                            COUNT(*) FILTER (WHERE processingstatus = 'Processing') AS ProcessingCount  
                        FROM emailnotifications  
                        WHERE receiveddatetime > NOW() - INTERVAL '1 hour' * @Hours;";

                    const string recentActivitySql = @"  
                        SELECT   
                            DATE_TRUNC('hour', receiveddatetime) AS Hour,  
                            COUNT(*) AS Count,  
                            processingstatus AS Status  
                        FROM emailnotifications  
                        WHERE receiveddatetime > NOW() - INTERVAL '1 hour' * @Hours  
                        GROUP BY DATE_TRUNC('hour', receiveddatetime), processingstatus  
                        ORDER BY Hour DESC, Status;";

                    var parameters = new { Hours = hours };

                    // ✅ Execute all queries in parallel for better performance  
                    var statusCountsTask = conn.QueryAsync<StatusCount>(statusCountsSql, parameters);
                    var totalTask = conn.ExecuteScalarAsync<int>(totalSql, parameters);
                    var retryStatsTask = conn.QuerySingleOrDefaultAsync<RetryStats>(retryStatsSql, parameters);
                    var performanceStatsTask = conn.QuerySingleOrDefaultAsync<PerformanceStats>(performanceStatsSql, parameters);
                    var recentActivityTask = conn.QueryAsync<HourlyActivity>(recentActivitySql, parameters);

                    await Task.WhenAll(
                        statusCountsTask,
                        totalTask,
                        retryStatsTask,
                        performanceStatsTask,
                        recentActivityTask);

                    var statusCounts = await statusCountsTask;
                    var total = await totalTask;
                    var retryStats = await retryStatsTask ?? new RetryStats();
                    var performanceStats = await performanceStatsTask ?? new PerformanceStats();
                    var recentActivity = await recentActivityTask;

                    // ✅ Calculate success rate  
                    var successRate = total > 0
                        ? Math.Round((double)performanceStats.SuccessCount / total * 100, 2)
                        : 0.0;

                    var statistics = new NotificationStatistics
                    {
                        Period = $"Last {hours} hours",
                        TimeWindowHours = hours,
                        TotalNotifications = total,
                        ByStatus = statusCounts.ToList(),
                        SuccessRate = successRate,
                        AverageRetryCount = Math.Round(retryStats.AverageRetryCount ?? 0, 2),
                        MaxRetryCount = retryStats.MaxRetryCount ?? 0,
                        NotificationsWithRetries = retryStats.NotificationsWithRetries ?? 0,
                        PendingCount = performanceStats.PendingCount,
                        ProcessingCount = performanceStats.ProcessingCount,
                        CompletedCount = performanceStats.SuccessCount,
                        FailedCount = performanceStats.FailedCount,
                        RecentActivity = recentActivity.ToList(),
                        Timestamp = DateTime.UtcNow,
                        CorrelationId = correlationId
                    };

                    _logger.LogInformation(
                        "✅ Statistics retrieved successfully. Total: {Total}, Success Rate: {SuccessRate}%, CorrelationId: {CorrelationId}",
                        total,
                        successRate,
                        correlationId);

                    _telemetry.TrackEvent("NotificationStats_Retrieved", new Dictionary<string, string>
                    {
                        { "CorrelationId", correlationId },
                        { "TimeWindowHours", hours.ToString() },
                        { "TotalNotifications", total.ToString() },
                        { "SuccessRate", successRate.ToString() }
                    });

                    var response = req.CreateResponse(HttpStatusCode.OK);
                    response.Headers.Add("Cache-Control", "public, max-age=60"); // ✅ Cache for 1 minute  
                    await response.WriteAsJsonAsync(statistics, cancellationToken: cancellationToken);

                    LogEnd(nameof(NotificationStatsFunction), new
                    {
                        correlationId,
                        total,
                        successRate,
                        hours
                    });

                    return response;
                }
                catch (ArgumentException argEx)
                {
                    _logger.LogWarning(
                        argEx,
                        "⚠️ Invalid argument in statistics request. CorrelationId: {CorrelationId}",
                        correlationId);

                    _telemetry.TrackException(argEx, new Dictionary<string, string>
                    {
                        { "CorrelationId", correlationId },
                        { "ErrorType", "InvalidArgument" }
                    });

                    return await BadRequest(req, $"Invalid request: {argEx.Message}");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "⚠️ Statistics request cancelled. CorrelationId: {CorrelationId}",
                        correlationId);

                    return await InternalServerError(req, "Request was cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "❌ Error retrieving statistics. CorrelationId: {CorrelationId}, Error: {ErrorMessage}",
                        correlationId,
                        ex.Message);

                    _telemetry.TrackException(ex, new Dictionary<string, string>
                    {
                        { "CorrelationId", correlationId },
                        { "Operation", "GetNotificationStats" },
                        { "ErrorType", ex.GetType().Name }
                    });

                    return await InternalServerError(req, "Error retrieving statistics. Please try again later.");
                }
            }
        }
    }
       
}