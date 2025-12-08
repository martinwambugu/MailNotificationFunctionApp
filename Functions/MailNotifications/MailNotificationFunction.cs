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
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Functions.MailNotifications
{
    /// <summary>  
    /// Azure Function endpoint for receiving Microsoft Graph mail notifications and validation requests.  
    /// </summary>  
    public class MailNotificationFunction : BaseFunction
    {
        private readonly INotificationHandler _handler;

        public MailNotificationFunction(
            INotificationHandler handler,
            ILogger<MailNotificationFunction> logger,
            ICustomTelemetry telemetry)
            : base(logger, telemetry)
        {
            _handler = handler;
        }

        /// <summary>  
        /// Receives and processes Microsoft Graph mail notifications and validation requests.  
        /// </summary>  
        /// <param name="req">The HTTP request containing the notification payload or validation token.</param>  
        /// <returns>HTTP response with validation token or success message.</returns>  
        [Function("MailNotification")]
        [OpenApiOperation(
            operationId: "MailNotification",
            tags: new[] { "MailNotifications" },
            Summary = "Receive and process Microsoft Graph mail notifications.",
            Description = "Handles Microsoft Graph webhook validation and mail notification payloads.")]
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
            HttpStatusCode.InternalServerError,
            Description = "Internal server error occurred.")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "notifications")] HttpRequestData req)
        {
            LogStart(nameof(MailNotificationFunction));

            try
            {
                // Handle Microsoft Graph webhook validation  
                var validationToken = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["validationToken"];
                if (!string.IsNullOrEmpty(validationToken))
                {
                    _logger.LogInformation("🔑 Validation token received: {Token}", validationToken);
                    var resp = req.CreateResponse(HttpStatusCode.OK);
                    await resp.WriteAsJsonAsync(new ValidationResponse { ValidationToken = validationToken });
                    LogEnd(nameof(MailNotificationFunction), new { validationToken });
                    return resp;
                }

                // Process notification payload  
                var rawBody = await new System.IO.StreamReader(req.Body).ReadToEndAsync();
                _logger.LogInformation("📨 Notification payload received. Length={Length}", rawBody.Length);

                var payload = JsonSerializer.Deserialize<GraphNotification>(rawBody);
                foreach (var item in payload?.Value ?? new List<GraphNotification.ChangeNotification>())
                {
                    await _handler.HandleAsync(item, rawBody);
                }

                var ok = req.CreateResponse(HttpStatusCode.OK);
                await ok.WriteAsJsonAsync(new { message = "Notifications processed successfully." });
                LogEnd(nameof(MailNotificationFunction));
                return ok;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in MailNotificationFunction.");
                _telemetry.TrackException(ex);
                return await InternalServerError(req, "An error occurred while processing notifications.");
            }
        }
    }
}