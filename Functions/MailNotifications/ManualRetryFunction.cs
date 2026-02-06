using MailNotificationFunctionApp.Infrastructure;
using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Services;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System.Net;

/// <summary>  
/// Manual trigger for retrying failed notifications with authentication.  
/// </summary>  
/// <remarks>  
/// <para>  
/// <b>Authentication:</b> Requires function key in query string (?code=xxx) or x-functions-key header.  
/// Function keys can be retrieved from Azure Portal → Function App → App Keys.  
/// </para>  
/// <para>  
/// <b>Use Cases:</b>  
/// - Manual intervention when automatic retries are failing  
/// - Testing retry logic during development  
/// - Emergency recovery after system outages  
/// </para>  
/// <para>  
/// <b>Security:</b>   
/// - Requires valid function key (not API key - different from webhook endpoint)  
/// - Rate limited by Azure Functions platform  
/// - Logs all retry attempts for audit trail  
/// </para>  
/// </remarks>  
public class ManualRetryFunction : BaseFunction
{
    private readonly NotificationRetryService _retryService;
    private readonly IConfiguration _config;

    public ManualRetryFunction(
        NotificationRetryService retryService,
        IConfiguration config,
        ILogger<ManualRetryFunction> logger,
        ICustomTelemetry telemetry)
        : base(logger, telemetry)
    {
        _retryService = retryService ?? throw new ArgumentNullException(nameof(retryService));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>  
    /// Manually triggers retry of failed notifications with authentication.  
    /// </summary>  
    /// <remarks>  
    /// <b>Authentication Methods:</b>  
    /// <list type="bullet">  
    /// <item>Query string: POST /api/notifications/retry?code=YOUR_FUNCTION_KEY</item>  
    /// <item>Header: POST /api/notifications/retry with x-functions-key: YOUR_FUNCTION_KEY</item>  
    /// </list>  
    /// </remarks>  
    [Function("ManualRetry")]
    [OpenApiOperation(
        operationId: "ManualRetry",
        tags: new[] { "Notifications" },
        Summary = "Manually trigger retry of failed notifications (requires authentication).",
        Description = "Immediately processes failed notifications without waiting for the timer. " +
                     "Requires function key authentication (?code=xxx or x-functions-key header).")]
    [OpenApiSecurity(
        "FunctionKeyAuth",
        SecuritySchemeType.ApiKey,
        Name = "code",
        In = OpenApiSecurityLocationType.Query,
        Description = "Function key for authentication (retrieve from Azure Portal → Function App → App Keys)")]
    [OpenApiParameter(
        name: "maxRetries",
        In = ParameterLocation.Query,
        Required = false,
        Type = typeof(int),
        Description = "Maximum number of notifications to retry (default: 100, max: 1000)")]
    [OpenApiParameter(
        name: "batchSize",
        In = ParameterLocation.Query,
        Required = false,
        Type = typeof(int),
        Description = "Number of notifications to process in parallel (default: 5, max: 20)")]
    [OpenApiResponseWithBody(
        HttpStatusCode.OK,
        "application/json",
        typeof(ManualRetryResponse),
        Description = "Retry completed successfully.")]
    [OpenApiResponseWithoutBody(
        HttpStatusCode.Unauthorized,
        Description = "Missing or invalid function key.")]
    [OpenApiResponseWithoutBody(
        HttpStatusCode.BadRequest,
        Description = "Invalid parameters (maxRetries or batchSize out of range).")]
    [OpenApiResponseWithoutBody(
        HttpStatusCode.TooManyRequests,
        Description = "Retry operation already in progress.")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "notifications/retry")]
        HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString();

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["Operation"] = "ManualRetry",
            ["Initiator"] = req.Headers.TryGetValues("X-Forwarded-For", out var ips)
                ? ips.FirstOrDefault()
                : "unknown"
        }))
        {
            LogStart(nameof(ManualRetryFunction));

            try
            {
                // ✅ Parse optional parameters  
                var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

                var maxRetries = 100;
                if (queryParams["maxRetries"] != null)
                {
                    if (!int.TryParse(queryParams["maxRetries"], out maxRetries) ||
                        maxRetries <= 0 ||
                        maxRetries > 1000)
                    {
                        _logger.LogWarning(
                            "⚠️ Invalid maxRetries parameter: {Value}. Must be between 1 and 1000.",
                            queryParams["maxRetries"]);

                        return await BadRequest(req, "maxRetries must be between 1 and 1000");
                    }
                }

                var batchSize = 5;
                if (queryParams["batchSize"] != null)
                {
                    if (!int.TryParse(queryParams["batchSize"], out batchSize) ||
                        batchSize <= 0 ||
                        batchSize > 20)
                    {
                        _logger.LogWarning(
                            "⚠️ Invalid batchSize parameter: {Value}. Must be between 1 and 20.",
                            queryParams["batchSize"]);

                        return await BadRequest(req, "batchSize must be between 1 and 20");
                    }
                }

                _logger.LogInformation(
                    "🔄 Manual retry triggered. MaxRetries: {MaxRetries}, BatchSize: {BatchSize}, CorrelationId: {CorrelationId}",
                    maxRetries,
                    batchSize,
                    correlationId);

                _telemetry.TrackEvent("ManualRetry_Started", new Dictionary<string, string>
                {
                    { "CorrelationId", correlationId },
                    { "MaxRetries", maxRetries.ToString() },
                    { "BatchSize", batchSize.ToString() }
                });

                // ✅ Execute retry with configurable parameters  
                var startTime = DateTime.UtcNow;
                var result = await _retryService.RetryFailedNotificationsAsync(
                    maxRetries: maxRetries,
                    batchSize: batchSize,
                    cancellationToken: cancellationToken);

                var duration = DateTime.UtcNow - startTime;

                _logger.LogInformation(
                    "✅ Manual retry completed. Retried: {Retried}, Succeeded: {Succeeded}, Failed: {Failed}, Duration: {Duration}ms, CorrelationId: {CorrelationId}",
                    result.TotalRetried,
                    result.SuccessCount,
                    result.FailureCount,
                    duration.TotalMilliseconds,
                    correlationId);

                _telemetry.TrackEvent("ManualRetry_Completed", new Dictionary<string, string>
                {
                    { "CorrelationId", correlationId },
                    { "TotalRetried", result.TotalRetried.ToString() },
                    { "SuccessCount", result.SuccessCount.ToString() },
                    { "FailureCount", result.FailureCount.ToString() },
                    { "DurationMs", duration.TotalMilliseconds.ToString("F0") }
                });

                _telemetry.TrackMetric("ManualRetry_Duration", duration.TotalMilliseconds, new Dictionary<string, string>
                {
                    { "CorrelationId", correlationId }
                });

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new ManualRetryResponse
                {
                    Message = "Retry completed successfully",
                    TotalRetried = result.TotalRetried,
                    SuccessCount = result.SuccessCount,
                    FailureCount = result.FailureCount,
                    DurationMs = (int)duration.TotalMilliseconds,
                    CorrelationId = correlationId,
                    Timestamp = DateTime.UtcNow
                }, cancellationToken: cancellationToken);

                LogEnd(nameof(ManualRetryFunction), new
                {
                    result.TotalRetried,
                    result.SuccessCount,
                    result.FailureCount,
                    durationMs = duration.TotalMilliseconds,
                    correlationId
                });

                return response;
            }
            catch (InvalidOperationException opEx) when (opEx.Message.Contains("already in progress"))
            {
                // ✅ Retry already running (concurrency check)  
                _logger.LogWarning(
                    opEx,
                    "⚠️ Retry operation already in progress. CorrelationId: {CorrelationId}",
                    correlationId);

                var response = req.CreateResponse(HttpStatusCode.TooManyRequests);
                await response.WriteAsJsonAsync(new
                {
                    error = "Retry operation already in progress",
                    message = "Please wait for the current retry operation to complete before starting another.",
                    correlationId = correlationId
                }, cancellationToken: cancellationToken);

                return response;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "⚠️ Manual retry operation was cancelled. CorrelationId: {CorrelationId}",
                    correlationId);

                return await InternalServerError(req, "Retry operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Error during manual retry. CorrelationId: {CorrelationId}",
                    correlationId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "ManualRetry" },
                    { "CorrelationId", correlationId }
                });

                return await InternalServerError(req, "Error during retry operation");
            }
        }
    }
}

/// <summary>  
/// Response model for manual retry operations.  
/// </summary>  
public class ManualRetryResponse
{
    /// <summary>Human-readable message.</summary>  
    public string Message { get; set; } = string.Empty;

    /// <summary>Total number of notifications attempted to retry.</summary>  
    public int TotalRetried { get; set; }

    /// <summary>Number of notifications successfully processed.</summary>  
    public int SuccessCount { get; set; }

    /// <summary>Number of notifications that failed during retry.</summary>  
    public int FailureCount { get; set; }

    /// <summary>Duration of retry operation in milliseconds.</summary>  
    public int DurationMs { get; set; }

    /// <summary>Correlation ID for tracking this operation.</summary>  
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Timestamp when retry completed (UTC).</summary>  
    public DateTime Timestamp { get; set; }
}