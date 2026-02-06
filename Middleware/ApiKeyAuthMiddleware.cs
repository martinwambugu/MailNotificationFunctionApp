using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MailNotificationFunctionApp.Middleware
{
    /// <summary>  
    /// API Key authentication middleware for Azure Functions Isolated Worker.  
    /// Checks for a configured static API key in request headers.  
    /// Skips authentication for:  
    /// <list type="bullet">  
    ///     <item>Swagger/OpenAPI documentation endpoints</item>  
    ///     <item>Microsoft Graph webhook requests (validation and notifications)</item>  
    /// </list>  
    /// Note: Microsoft Graph notifications are authenticated via client state validation in the handler,  
    /// not via API key headers.  
    /// </summary>  
    public class ApiKeyAuthMiddleware : IFunctionsWorkerMiddleware
    {
        private readonly ILogger<ApiKeyAuthMiddleware> _logger;
        private readonly IConfiguration _config;

        public ApiKeyAuthMiddleware(IConfiguration config, ILogger<ApiKeyAuthMiddleware> logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var req = await context.GetHttpRequestDataAsync();

            // Skip if not HTTP-triggered  
            if (req == null)
            {
                await next(context);
                return;
            }

            var path = req.Url.AbsolutePath?.ToLowerInvariant() ?? string.Empty;
            var method = req.Method?.ToUpperInvariant() ?? string.Empty;

            // ✅ CRITICAL: Bypass API key validation for Microsoft Graph webhook endpoint ONLY
            // Microsoft Graph sends:
            // 1. GET /notifications?validationToken=xyz (validation)
            // 2. POST /notifications (actual change notifications)
            // Neither includes custom headers like x-api-key
            // Security is provided by client state validation in the notification handler
            if (IsGraphWebhookEndpoint(path, method))
            {
                _logger.LogInformation(
                    "🔓 Bypassing API key check for Microsoft Graph webhook endpoint. Method: {Method}, Path: {Path}",
                    method,
                    path);
                await next(context);
                return;
            }

            // ✅ Skip authentication for Swagger/OpenAPI endpoints
            if (IsDocumentationEndpoint(path))
            {
                _logger.LogDebug("Skipping API key authentication for documentation endpoint: {Path}", path);
                await next(context);
                return;
            }

            // ✅ Check if Auth mode is API Key  
            var authMode = _config["Auth:Mode"] ?? _config["Auth__Mode"];
            if (!string.Equals(authMode, "apikey", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Auth:Mode is not set to 'apikey' (current value: '{AuthMode}'). API key authentication skipped.",
                    authMode);
                await next(context);
                return;
            }

            // ✅ Load expected API key from configuration (support both : and __ for Azure)  
            var expectedKey = _config["Auth:ApiKey"] ?? _config["Auth__ApiKey"];
            if (string.IsNullOrWhiteSpace(expectedKey))
            {
                _logger.LogError(
                    "❌ API key is not configured. Set 'Auth__ApiKey' in Azure App Settings or 'Auth:ApiKey' in local.settings.json.");
                await WriteUnauthorizedAsync(context, "Server authentication configuration error.");
                return;
            }

            // ✅ Check if header exists  
            if (!req.Headers.TryGetValues("x-api-key", out var apiKeyHeaders))
            {
                _logger.LogWarning("❌ Missing x-api-key header. Path: {Path}", path);
                await WriteUnauthorizedAsync(context, "Missing x-api-key header");
                return;
            }

            var providedKey = apiKeyHeaders.FirstOrDefault()?.Trim();
            var expectedKeyTrimmed = expectedKey.Trim();

            // ✅ Compare keys (case-insensitive, trimmed)  
            if (!string.Equals(providedKey, expectedKeyTrimmed, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("❌ Invalid API key provided. Path: {Path}", path);
                await WriteUnauthorizedAsync(context, "Invalid API key");
                return;
            }

            _logger.LogInformation("✅ API Key authentication succeeded. Path: {Path}", path);
            await next(context);
        }

        /// <summary>
        /// Determines if the request is from Microsoft Graph webhook (validation or notification).
        /// </summary>
        /// <remarks>
        /// Microsoft Graph requires anonymous access because:
        /// 1. Subscription validation uses GET with validationToken query parameter
        /// 2. Webhook notifications use POST without custom headers
        /// 3. Security is enforced via client state validation in NotificationHandler
        /// </remarks>
        private static bool IsGraphWebhookEndpoint(string path, string method)
        {
            // Only bypass the exact /api/notifications endpoint (not sub-paths like /api/notifications/stats)
            // Accept both GET (validation) and POST (notifications)

            // Check for route prefix from host.json
            var isExactMatch = path.Equals("/api/notifications", StringComparison.OrdinalIgnoreCase) ||
                              path.Equals("/service/mailnotificationfunction/api/notifications", StringComparison.OrdinalIgnoreCase);

            var isGetOrPost = method == "GET" || method == "POST";

            return isExactMatch && isGetOrPost;
        }

        /// <summary>
        /// Determines if the request is for API documentation.
        /// </summary>
        private static bool IsDocumentationEndpoint(string path)
        {
            return path.Contains("swagger", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("openapi", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("oauth2-redirect", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task WriteUnauthorizedAsync(FunctionContext context, string message)
        {
            var req = await context.GetHttpRequestDataAsync();
            if (req != null)
            {
                var res = req.CreateResponse(HttpStatusCode.Unauthorized);
                await res.WriteAsJsonAsync(new { error = message });
                context.GetInvocationResult().Value = res;
            }
        }
    }
}