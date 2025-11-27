using System;
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
    /// Skips authentication for Swagger/OpenAPI documentation endpoints.  
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

            // ✅ Skip authentication for Swagger/OpenAPI endpoints  
            if (path.Contains("swagger", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("openapi", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("oauth2-redirect", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping API key authentication for documentation endpoint: {Path}", path);
                await next(context);
                return;
            }

            // ✅ Check if Auth mode is API Key  
            var authMode = _config["Auth:Mode"] ?? _config["Auth__Mode"];
            if (!string.Equals(authMode, "apikey", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Auth:Mode is not set to 'apikey' (current value: '{AuthMode}'). API key authentication skipped.", authMode);
                await next(context);
                return;
            }

            // ✅ Load expected API key from configuration (support both : and __ for Azure)  
            var expectedKey = _config["Auth:ApiKey"] ?? _config["Auth__ApiKey"];

            if (string.IsNullOrWhiteSpace(expectedKey))
            {
                _logger.LogError("❌ API key is not configured. Set 'Auth__ApiKey' in Azure App Settings or 'Auth:ApiKey' in local.settings.json.");
                await WriteUnauthorizedAsync(context, "Server authentication configuration error.");
                return;
            }

            // ✅ Check if header exists  
            if (!req.Headers.TryGetValues("x-api-key", out var apiKeyHeaders))
            {
                _logger.LogWarning("❌ Missing x-api-key header.");
                await WriteUnauthorizedAsync(context, "Missing x-api-key header");
                return;
            }

            var providedKey = apiKeyHeaders.FirstOrDefault()?.Trim();
            var expectedKeyTrimmed = expectedKey.Trim();

            // ✅ Compare keys (case-insensitive, trimmed)  
            if (!string.Equals(providedKey, expectedKeyTrimmed, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("❌ Invalid API key provided.");
                await WriteUnauthorizedAsync(context, "Invalid API key");
                return;
            }

            _logger.LogInformation("✅ API Key authentication succeeded.");
            await next(context);
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