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
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Functions.MailNotifications
{
    /// <summary>  
    /// Query notification status and history.  
    /// </summary>  
    public class NotificationQueryFunction : BaseFunction
    {
        private readonly IEmailNotificationRepository _repo;

        public NotificationQueryFunction(
            IEmailNotificationRepository repo,
            ILogger<NotificationQueryFunction> logger,
            ICustomTelemetry telemetry)
            : base(logger, telemetry)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        }

        /// <summary>  
        /// Get notification by ID.  
        /// </summary>  
        [Function("GetNotification")]
        [OpenApiOperation(
            operationId: "GetNotification",
            tags: new[] { "Notifications" },
            Summary = "Retrieve a specific notification by ID.",
            Description = "Returns notification details including processing status and retry count.")]
        [OpenApiSecurity(
            "ApiKeyAuth",
            SecuritySchemeType.ApiKey,
            Name = "x-api-key",
            In = OpenApiSecurityLocationType.Header)]
        [OpenApiParameter(
            name: "notificationId",
            In = ParameterLocation.Path,
            Required = true,
            Type = typeof(string),
            Description = "The notification ID.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "application/json",
            typeof(EmailNotification),
            Description = "Notification found.")]
        [OpenApiResponseWithoutBody(
            HttpStatusCode.NotFound,
            Description = "Notification not found.")]
        public async Task<HttpResponseData> GetNotification(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "notifications/{notificationId}")]
            HttpRequestData req,
            string notificationId,
            CancellationToken cancellationToken)
        {
            LogStart(nameof(GetNotification), new { notificationId });

            try
            {
                var notification = await _repo.GetNotificationByIdAsync(notificationId, cancellationToken);

                if (notification == null)
                {
                    _logger.LogWarning("⚠️ Notification not found: {NotificationId}", notificationId);
                    return await NotFound(req, $"Notification '{notificationId}' not found");
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(notification, cancellationToken: cancellationToken);

                LogEnd(nameof(GetNotification), new { notificationId, status = notification.ProcessingStatus });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error retrieving notification: {NotificationId}", notificationId);
                _telemetry.TrackException(ex);
                return await InternalServerError(req, "Error retrieving notification");
            }
        }

        /// <summary>  
        /// Get notifications by subscription ID.  
        /// </summary>  
        [Function("GetNotificationsBySubscription")]
        [OpenApiOperation(
            operationId: "GetNotificationsBySubscription",
            tags: new[] { "Notifications" },
            Summary = "Retrieve notifications for a specific subscription.",
            Description = "Returns up to 100 most recent notifications for the given subscription ID.")]
        [OpenApiSecurity(
            "ApiKeyAuth",
            SecuritySchemeType.ApiKey,
            Name = "x-api-key",
            In = OpenApiSecurityLocationType.Header)]
        [OpenApiParameter(
            name: "subscriptionId",
            In = ParameterLocation.Path,
            Required = true,
            Type = typeof(string),
            Description = "The subscription ID.")]
        [OpenApiParameter(
            name: "limit",
            In = ParameterLocation.Query,
            Required = false,
            Type = typeof(int),
            Description = "Maximum number of results (default: 100, max: 1000).")]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "application/json",
            typeof(IEnumerable<EmailNotification>),
            Description = "Notifications retrieved successfully.")]
        public async Task<HttpResponseData> GetNotificationsBySubscription(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "notifications/subscription/{subscriptionId}")]
            HttpRequestData req,
            string subscriptionId,
            CancellationToken cancellationToken)
        {
            LogStart(nameof(GetNotificationsBySubscription), new { subscriptionId });

            try
            {
                var limitStr = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["limit"];
                var limit = int.TryParse(limitStr, out var l) ? l : 100;

                var notifications = await _repo.GetNotificationsBySubscriptionAsync(
                    subscriptionId,
                    limit,
                    cancellationToken);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    subscriptionId,
                    count = notifications.Count(),
                    notifications
                }, cancellationToken: cancellationToken);

                LogEnd(nameof(GetNotificationsBySubscription), new { subscriptionId, count = notifications.Count() });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error retrieving notifications for subscription: {SubscriptionId}", subscriptionId);
                _telemetry.TrackException(ex);
                return await InternalServerError(req, "Error retrieving notifications");
            }
        }

        /// <summary>  
        /// Get pending notifications (for monitoring).  
        /// </summary>  
        [Function("GetPendingNotifications")]
        [OpenApiOperation(
            operationId: "GetPendingNotifications",
            tags: new[] { "Notifications" },
            Summary = "Retrieve pending/failed notifications.",
            Description = "Returns notifications that are pending or failed and eligible for retry.")]
        [OpenApiSecurity(
            "ApiKeyAuth",
            SecuritySchemeType.ApiKey,
            Name = "x-api-key",
            In = OpenApiSecurityLocationType.Header)]
        [OpenApiParameter(
            name: "maxRetryCount",
            In = ParameterLocation.Query,
            Required = false,
            Type = typeof(int),
            Description = "Maximum retry count threshold (default: 3, max: 10).")]
        [OpenApiParameter(
            name: "batchSize",
            In = ParameterLocation.Query,
            Required = false,
            Type = typeof(int),
            Description = "Maximum number of notifications to retrieve (default: 100, max: 100).")]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "application/json",
            typeof(IEnumerable<EmailNotification>),
            Description = "Pending notifications retrieved successfully.")]
        public async Task<HttpResponseData> GetPendingNotifications(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "notifications/pending")]
            HttpRequestData req,
            CancellationToken cancellationToken)
        {
            LogStart(nameof(GetPendingNotifications));

            try
            {
                // ✅ Parse query parameters  
                var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

                // ✅ Parse maxRetryCount (default: 3, max: 10)  
                var maxRetryCount = 3;
                if (queryParams["maxRetryCount"] != null)
                {
                    if (!int.TryParse(queryParams["maxRetryCount"], out maxRetryCount) ||
                        maxRetryCount < 0 ||
                        maxRetryCount > 10)
                    {
                        _logger.LogWarning(
                            "⚠️ Invalid maxRetryCount parameter: {Value}. Using default: 3",
                            queryParams["maxRetryCount"]);
                        maxRetryCount = 3;
                    }
                }

                // ✅ Parse batchSize (default: 100, max: 100)  
                var batchSize = 100;
                if (queryParams["batchSize"] != null)
                {
                    if (!int.TryParse(queryParams["batchSize"], out batchSize) ||
                        batchSize <= 0 ||
                        batchSize > 100)
                    {
                        _logger.LogWarning(
                            "⚠️ Invalid batchSize parameter: {Value}. Using default: 100",
                            queryParams["batchSize"]);
                        batchSize = 100;
                    }
                }

                _logger.LogInformation(
                    "📋 Querying pending notifications. MaxRetryCount: {MaxRetryCount}, BatchSize: {BatchSize}",
                    maxRetryCount,
                    batchSize);

                // ✅ Call repository with parsed parameters  
                var notifications = await _repo.GetAndLockPendingNotificationsAsync(
                    batchSize: batchSize,
                    maxRetryCount: maxRetryCount,
                    cancellationToken: cancellationToken);

                var notificationList = notifications.ToList();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    count = notificationList.Count,
                    maxRetryCount = maxRetryCount,
                    batchSize = batchSize,
                    notifications = notificationList
                }, cancellationToken: cancellationToken);

                LogEnd(nameof(GetPendingNotifications), new
                {
                    count = notificationList.Count,
                    maxRetryCount,
                    batchSize
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error retrieving pending notifications");
                _telemetry.TrackException(ex);
                return await InternalServerError(req, "Error retrieving pending notifications");
            }
        }
    }
}