using MailNotificationFunctionApp.Extensions;
using MailNotificationFunctionApp.Infrastructure;
using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
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
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Functions.MailNotifications
{
    /// <summary>  
    /// Azure Function endpoint for receiving Microsoft Graph mail notifications.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// <b>Security Model:</b>  
    /// Microsoft Graph webhooks use client state validation instead of API keys.  
    /// Each notification includes a clientState field that must match the value  
    /// set during subscription creation. This proves authenticity without requiring  
    /// Microsoft to send authentication headers.  
    /// </para>  
    /// <para>  
    /// <b>Request Types:</b>  
    /// <list type="number">  
    ///     <item><b>Validation (GET/POST with validationToken):</b> One-time validation during subscription creation.</item>  
    ///     <item><b>Notifications (POST):</b> Webhook notifications when mailbox events occur.</item>  
    /// </list>  
    /// </para>  
    /// <para>  
    /// <b>Security Features:</b>  
    /// - Client state validation (prevents spoofing)  
    /// - Request size limits (prevents DoS)  
    /// - Idempotency checks (prevents duplicate processing)  
    /// - HTTPS enforcement (Azure Functions requirement)  
    /// </para>  
    /// </remarks>  
    public class MailNotificationFunction : BaseFunction
    {
        private readonly INotificationHandler _handler;
        private const long MaxRequestSizeBytes = 1 * 1024 * 1024; // 1MB  

        public MailNotificationFunction(
            INotificationHandler handler,
            ILogger<MailNotificationFunction> logger,
            ICustomTelemetry telemetry)
            : base(logger, telemetry)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>  
        /// Receives and processes Microsoft Graph mail notifications with client state validation.  
        /// </summary>  
        /// <remarks>  
        /// <para>  
        /// <b>Authentication:</b> Uses AuthorizationLevel.Anonymous because Microsoft Graph  
        /// does not send API keys. Security is enforced through client state validation  
        /// (comparing notification.clientState with stored subscription.clientState).  
        /// </para>  
        /// <para>  
        /// <b>Endpoint Protection:</b>   
        /// - HTTPS is enforced by Azure Functions (HTTP not allowed)  
        /// - Request size limits prevent DoS attacks  
        /// - Client state validation prevents spoofing  
        /// - Idempotency checks prevent duplicate processing  
        /// </para>  
        /// </remarks>  
        [Function("MailNotification")]
        [OpenApiOperation(
            operationId: "MailNotification",
            tags: new[] { "MailNotifications" },
            Summary = "Receive and process Microsoft Graph mail notifications.",
            Description = "Handles Microsoft Graph webhook validation (GET) and mail notification payloads (POST) with client state validation. " +
                         "No API key required - Microsoft Graph uses client state validation for security.")]
        [OpenApiRequestBody(
            "application/json",
            typeof(GraphNotification),
            Description = "Graph notification payload (POST only).")]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "text/plain",
            typeof(string),
            Description = "Validation token (GET) or success response (POST).")]
        [OpenApiResponseWithoutBody(
            HttpStatusCode.Unauthorized,
            Description = "Client state validation failed (spoofing attempt detected).")]
        [OpenApiResponseWithoutBody(
            HttpStatusCode.BadRequest,
            Description = "Invalid request format or missing required fields.")]
        [OpenApiResponseWithoutBody(
            HttpStatusCode.InternalServerError,
            Description = "Internal server error occurred.")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "notifications")]
            HttpRequestData req,
            CancellationToken cancellationToken)
        {
            // ✅ Generate correlation ID for request tracking  
            var correlationId = req.Headers.TryGetValues("X-Correlation-ID", out var corrIds)
                ? corrIds.FirstOrDefault()
                : Guid.NewGuid().ToString();

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["RequestMethod"] = req.Method,
                ["RequestPath"] = req.Url.AbsolutePath,
                ["RemoteIp"] = req.Headers.TryGetValues("X-Forwarded-For", out var ips)
                    ? ips.FirstOrDefault()
                    : "unknown"
            }))
            {
                LogStart(nameof(MailNotificationFunction));

                try
                {
                    // ✅ Handle Microsoft Graph webhook validation (GET or POST with validationToken)  
                    var validationToken = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["validationToken"];
                    if (!string.IsNullOrEmpty(validationToken))
                    {
                        _logger.LogInformation(
                            "🔑 Validation token received via {Method}: {Token}",
                            req.Method,
                            validationToken);

                        var resp = req.CreateResponse(HttpStatusCode.OK);
                        resp.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                        await resp.WriteStringAsync(validationToken, cancellationToken);

                        _telemetry.TrackEvent("MailNotification_ValidationTokenReturned", new Dictionary<string, string>
                        {
                            { "CorrelationId", correlationId },
                            { "Method", req.Method },
                            { "TokenLength", validationToken.Length.ToString() }
                        });

                        _logger.LogInformation(
                            "✅ Validation token echoed back to Microsoft Graph. CorrelationId: {CorrelationId}",
                            correlationId);

                        LogEnd(nameof(MailNotificationFunction), new { validationToken, method = req.Method });
                        return resp;
                    }

                    // ✅ Handle POST requests (actual notifications)  
                    if (!req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "⚠️ Unsupported HTTP method: {Method}. CorrelationId: {CorrelationId}",
                            req.Method,
                            correlationId);

                        return await BadRequest(req, $"Unsupported HTTP method: {req.Method}. Expected POST or GET with validationToken.");
                    }

                    // ✅ Validate Content-Length to prevent DoS attacks  
                    if (req.Headers.TryGetValues("Content-Length", out var contentLengthValues))
                    {
                        if (long.TryParse(contentLengthValues.FirstOrDefault(), out var contentLength))
                        {
                            if (contentLength > MaxRequestSizeBytes)
                            {
                                _logger.LogWarning(
                                    "⚠️ Request body too large: {Size} bytes (max: {MaxSize} bytes). CorrelationId: {CorrelationId}",
                                    contentLength,
                                    MaxRequestSizeBytes,
                                    correlationId);

                                _telemetry.TrackEvent("MailNotification_RequestTooLarge", new Dictionary<string, string>
                                {
                                    { "CorrelationId", correlationId },
                                    { "RequestSize", contentLength.ToString() },
                                    { "MaxSize", MaxRequestSizeBytes.ToString() }
                                });

                                return await BadRequest(req,
                                    $"Request body too large. Maximum size: {MaxRequestSizeBytes / 1024 / 1024}MB");
                            }
                        }
                    }

                    // ✅ Read request body with size limit  
                    string rawBody;
                    try
                    {
                        using var limitedStream = new LimitedStream(req.Body, MaxRequestSizeBytes);
                        using var reader = new System.IO.StreamReader(limitedStream);
                        rawBody = await reader.ReadToEndAsync(cancellationToken);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("exceeded maximum length"))
                    {
                        _logger.LogWarning(
                            ex,
                            "⚠️ Request body exceeded size limit. CorrelationId: {CorrelationId}",
                            correlationId);

                        return await BadRequest(req, "Request body too large");
                    }

                    if (string.IsNullOrWhiteSpace(rawBody))
                    {
                        _logger.LogWarning(
                            "⚠️ Empty request body received. CorrelationId: {CorrelationId}",
                            correlationId);

                        return await BadRequest(req, "Request body is empty");
                    }

                    _logger.LogInformation(
                        "📨 Notification payload received. Length={Length}, CorrelationId={CorrelationId}",
                        rawBody.Length,
                        correlationId);

                    // ✅ Parse JSON payload
                    GraphNotification? payload;
                    try
                    {
                        payload = JsonSerializer.Deserialize<GraphNotification>(
                            rawBody,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true,
                                MaxDepth = 32,  // ✅ Prevent deeply nested JSON attacks (JSON bomb protection)
                                AllowTrailingCommas = false,
                                ReadCommentHandling = JsonCommentHandling.Disallow
                            });
                    }
                    catch (JsonException jsonEx)
                    {
                        _logger.LogError(
                            jsonEx,
                            "❌ Failed to deserialize notification payload. CorrelationId: {CorrelationId}",
                            correlationId);

                        _telemetry.TrackException(jsonEx, new Dictionary<string, string>
                        {
                            { "CorrelationId", correlationId },
                            { "Operation", "DeserializePayload" }
                        });

                        return await BadRequest(req, "Invalid JSON payload");
                    }

                    if (payload?.Value == null || !payload.Value.Any())
                    {
                        _logger.LogWarning(
                            "⚠️ Notification payload contains no items. CorrelationId: {CorrelationId}",
                            correlationId);

                        return await BadRequest(req, "Notification payload is empty");
                    }

                    _logger.LogInformation(
                        "📦 Processing {Count} notification(s). CorrelationId: {CorrelationId}",
                        payload.Value.Count,
                        correlationId);

                    // ✅ Process notifications with concurrency limit (max 5 parallel)  
                    var semaphore = new SemaphoreSlim(5);
                   
                    var tasks = payload.Value.Select(async item =>
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        try
                        {
                            // ✅ FIXED: Convert GraphNotification.ChangeNotification to NotificationItem  
                            var notificationItem = item.ToNotificationItem(); // ✅ ADD THIS LINE  

                            // ✅ Client state validation happens inside HandleAsync  
                            return await _handler.HandleAsync(
                                notificationItem, // ✅ CHANGED: Pass NotificationItem instead of ChangeNotification  
                                rawBody,
                                cancellationToken);
                        }
                        catch (SecurityException secEx)
                        {
                            _logger.LogError(
                                secEx,
                                "🚨 Client state validation failed for SubscriptionId: {SubscriptionId}. CorrelationId: {CorrelationId}",
                                item.SubscriptionId,
                                correlationId);

                            _telemetry.TrackException(secEx, new Dictionary<string, string>
                            {
                                { "CorrelationId", correlationId },
                                { "SubscriptionId", item.SubscriptionId ?? "unknown" },
                                { "ErrorType", "ClientStateValidationFailed" }
                            });

                            throw; // Re-throw to fail entire batch (security violation)  
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "❌ Error processing notification item. SubscriptionId: {SubscriptionId}, CorrelationId: {CorrelationId}",
                                item.SubscriptionId,
                                correlationId);

                            _telemetry.TrackException(ex, new Dictionary<string, string>
                            {
                                { "CorrelationId", correlationId },
                                { "SubscriptionId", item.SubscriptionId ?? "unknown" }
                            });

                            return false; // Mark as failed but continue processing other notifications  
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    bool[] results;
                    try
                    {
                        results = await Task.WhenAll(tasks);
                    }
                    catch (SecurityException)
                    {
                        // ✅ Client state validation failed - return 401 to alert Microsoft Graph  
                        _logger.LogError(
                            "🚨 Client state validation failed for one or more notifications. " +
                            "Returning 401 to Microsoft Graph. CorrelationId: {CorrelationId}",
                            correlationId);

                        _telemetry.TrackEvent("MailNotification_SecurityViolation", new Dictionary<string, string>
                        {
                            { "CorrelationId", correlationId },
                            { "NotificationCount", payload.Value.Count.ToString() }
                        });

                        return await Unauthorized(req, "Client state validation failed. Possible spoofing attempt detected.");
                    }

                    var processedCount = results.Count(r => r);
                    var failedCount = results.Count(r => !r);

                    _logger.LogInformation(
                        "✅ Notification processing complete. " +
                        "Processed: {Processed}, Failed: {Failed}, Total: {Total}, CorrelationId: {CorrelationId}",
                        processedCount,
                        failedCount,
                        payload.Value.Count,
                        correlationId);

                    _telemetry.TrackMetric("MailNotifications_Processed", processedCount, new Dictionary<string, string>
                    {
                        { "CorrelationId", correlationId }
                    });

                    _telemetry.TrackMetric("MailNotifications_Failed", failedCount, new Dictionary<string, string>
                    {
                        { "CorrelationId", correlationId }
                    });

                    // ✅ Return success even if some notifications failed (Microsoft Graph expects 2xx for retries)  
                    var ok = req.CreateResponse(HttpStatusCode.OK);
                    await ok.WriteAsJsonAsync(new
                    {
                        message = "Notifications processed successfully.",
                        processed = processedCount,
                        failed = failedCount,
                        total = payload.Value.Count,
                        correlationId = correlationId
                    }, cancellationToken: cancellationToken);

                    LogEnd(nameof(MailNotificationFunction), new
                    {
                        processedCount,
                        failedCount,
                        totalCount = payload.Value.Count,
                        correlationId
                    });

                    return ok;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "⚠️ Operation was cancelled. CorrelationId: {CorrelationId}",
                        correlationId);

                    return await InternalServerError(req, "Operation was cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "❌ Unhandled error in MailNotificationFunction. CorrelationId: {CorrelationId}",
                        correlationId);

                    _telemetry.TrackException(ex, new Dictionary<string, string>
                    {
                        { "CorrelationId", correlationId },
                        { "Operation", "MailNotificationFunction" }
                    });

                    return await InternalServerError(req, "An error occurred while processing notifications.");
                }
            }
        }
    }

    /// <summary>  
    /// Stream wrapper that enforces maximum read length to prevent DoS attacks.  
    /// </summary>  
    public class LimitedStream : System.IO.Stream
    {
        private readonly System.IO.Stream _innerStream;
        private readonly long _maxLength;
        private long _totalBytesRead;

        public LimitedStream(System.IO.Stream innerStream, long maxLength)
        {
            _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            _maxLength = maxLength;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var bytesRead = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
            _totalBytesRead += bytesRead;

            if (_totalBytesRead > _maxLength)
            {
                throw new InvalidOperationException(
                    $"Stream exceeded maximum length of {_maxLength} bytes");
            }

            return bytesRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _innerStream.Read(buffer, offset, count);
            _totalBytesRead += bytesRead;

            if (_totalBytesRead > _maxLength)
            {
                throw new InvalidOperationException(
                    $"Stream exceeded maximum length of {_maxLength} bytes");
            }

            return bytesRead;
        }

        // Required Stream overrides  
        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => _innerStream.Flush();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}