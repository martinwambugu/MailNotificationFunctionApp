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
    /// Azure Function endpoint for receiving Microsoft Graph mail notifications and validation requests.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This function handles two types of requests:  
    /// <list type="number">  
    ///     <item><b>Validation Requests:</b> Microsoft Graph sends a validation token during subscription creation.</item>  
    ///     <item><b>Change Notifications:</b> Actual webhook notifications when mailbox events occur.</item>  
    /// </list>  
    /// </para>  
    /// <para>  
    /// <b>Security:</b> All notifications are validated using the client state value to prevent spoofing attacks.  
    /// </para>  
    /// </remarks>  
    public class MailNotificationFunction : BaseFunction
    {
        private readonly INotificationHandler _handler;

        public MailNotificationFunction(
            INotificationHandler handler,
            ILogger<MailNotificationFunction> logger,
            ICustomTelemetry telemetry)
            : base(logger, telemetry)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>  
        /// Receives and processes Microsoft Graph mail notifications and validation requests.  
        /// </summary>  
        [Function("MailNotification")]
        [OpenApiOperation(
            operationId: "MailNotification",
            tags: new[] { "MailNotifications" },
            Summary = "Receive and process Microsoft Graph mail notifications.",
            Description = "Handles Microsoft Graph webhook validation and mail notification payloads with client state validation.")]
        [OpenApiSecurity(
            "ApiKeyAuth",
            SecuritySchemeType.ApiKey,
            Name = "x-api-key",
            In = OpenApiSecurityLocationType.Header)]
        [OpenApiRequestBody(
            "application/json",
            typeof(GraphNotification),
            Description = "Graph notification payload.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "application/json",
            typeof(ValidationResponse),
            Description = "Validation or success response.")]
        [OpenApiResponseWithoutBody(
            HttpStatusCode.Unauthorized,
            Description = "Client state validation failed.")]
        [OpenApiResponseWithoutBody(
            HttpStatusCode.InternalServerError,
            Description = "Internal server error occurred.")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "notifications")]
            HttpRequestData req,
            CancellationToken cancellationToken)
        {
            LogStart(nameof(MailNotificationFunction));

            try
            {
                // ✅ Handle Microsoft Graph webhook validation  
                var validationToken = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["validationToken"];

                if (!string.IsNullOrEmpty(validationToken))
                {
                    _logger.LogInformation("🔑 Validation token received: {Token}", validationToken);

                    var resp = req.CreateResponse(HttpStatusCode.OK);
                    resp.Headers.Add("Content-Type", "text/plain");
                    await resp.WriteStringAsync(validationToken, cancellationToken);

                    _telemetry.TrackEvent("MailNotification_ValidationTokenReturned", new Dictionary<string, string>
                    {
                        { "Token", validationToken }
                    });

                    LogEnd(nameof(MailNotificationFunction), new { validationToken });
                    return resp;
                }

                // ✅ Process notification payload  
                var rawBody = await new System.IO.StreamReader(req.Body).ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(rawBody))
                {
                    _logger.LogWarning("⚠️ Empty request body received");
                    return await BadRequest(req, "Request body is empty");
                }

                _logger.LogInformation("📨 Notification payload received. Length={Length}", rawBody.Length);

                GraphNotification? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<GraphNotification>(
                        rawBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "❌ Failed to deserialize notification payload");
                    _telemetry.TrackException(jsonEx);
                    return await BadRequest(req, "Invalid JSON payload");
                }

                if (payload?.Value == null || !payload.Value.Any())
                {
                    _logger.LogWarning("⚠️ Notification payload contains no items");
                    return await BadRequest(req, "Notification payload is empty");
                }

                var processedCount = 0;
                var failedCount = 0;

                foreach (var item in payload.Value)
                {
                    try
                    {
                        var success = await _handler.HandleAsync(item, rawBody, cancellationToken);

                        if (success)
                            processedCount++;
                        else
                            failedCount++;
                    }
                    catch (SecurityException secEx)
                    {
                        _logger.LogError(secEx, "🚨 Security validation failed for notification");
                        _telemetry.TrackException(secEx, new Dictionary<string, string>
                        {
                            { "SubscriptionId", item.SubscriptionId ?? "unknown" }
                        });

                        failedCount++;

                        // ✅ Return 401 for security failures to alert Microsoft Graph  
                        return await Unauthorized(req, "Client state validation failed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error processing notification item");
                        _telemetry.TrackException(ex);
                        failedCount++;
                    }
                }

                _logger.LogInformation(
                    "✅ Notification processing complete. Processed: {Processed}, Failed: {Failed}",
                    processedCount, failedCount);

                _telemetry.TrackMetric("MailNotifications_Processed", processedCount);
                _telemetry.TrackMetric("MailNotifications_Failed", failedCount);

                var ok = req.CreateResponse(HttpStatusCode.OK);
                await ok.WriteAsJsonAsync(new
                {
                    message = "Notifications processed successfully.",
                    processed = processedCount,
                    failed = failedCount
                }, cancellationToken: cancellationToken);

                LogEnd(nameof(MailNotificationFunction), new { processedCount, failedCount });
                return ok;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ Operation was cancelled");
                return await InternalServerError(req, "Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unhandled error in MailNotificationFunction");
                _telemetry.TrackException(ex);
                return await InternalServerError(req, "An error occurred while processing notifications.");
            }
        }
    }
}