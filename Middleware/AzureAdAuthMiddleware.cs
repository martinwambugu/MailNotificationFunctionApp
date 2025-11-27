using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MailNotificationFunctionApp.Middleware
{
    /// <summary>  
    /// Azure AD JWT authentication middleware for production environment.  
    /// Validates Bearer tokens against Azure AD tenant and app registration.  
    /// </summary>  
    public class AzureAdAuthMiddleware : IFunctionsWorkerMiddleware
    {
        private readonly ILogger<AzureAdAuthMiddleware> _logger;
        private readonly IConfiguration _config;
        private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;

        public AzureAdAuthMiddleware(IConfiguration config, ILogger<AzureAdAuthMiddleware> logger)
        {
            _config = config;
            _logger = logger;

            var tenantId = _config["AzureAd:TenantId"];
            if (string.IsNullOrEmpty(tenantId))
            {
                throw new InvalidOperationException("AzureAd:TenantId is missing in configuration.");
            }

            var authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            var metadataAddress = $"{authority}/.well-known/openid-configuration";

            _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever()
            );
        }

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var req = await context.GetHttpRequestDataAsync();

            // If it's not an HTTP-triggered function, skip auth  
            if (req == null)
            {
                await next(context);
                return;
            }

            if (!req.Headers.TryGetValues("Authorization", out var authHeaders))
            {
                await WriteUnauthorizedAsync(context, "Missing Authorization header");
                return;
            }

            var bearerToken = authHeaders.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(bearerToken) || !bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                await WriteUnauthorizedAsync(context, "Invalid Authorization header format. Expected 'Bearer <token>'.");
                return;
            }

            var token = bearerToken.Substring("Bearer ".Length).Trim();

            try
            {
                var openIdConfig = await _configurationManager.GetConfigurationAsync(CancellationToken.None);

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = new[]
                    {
                        $"https://login.microsoftonline.com/{_config["AzureAd:TenantId"]}/v2.0",
                        $"https://sts.windows.net/{_config["AzureAd:TenantId"]}/"
                    },
                    ValidateAudience = true,
                    ValidAudience = _config["AzureAd:Audience"] ?? _config["AzureAd:ClientId"],
                    ValidateLifetime = true,
                    IssuerSigningKeys = openIdConfig.SigningKeys,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(2)
                };

                var handler = new JwtSecurityTokenHandler();
                var principal = handler.ValidateToken(token, validationParameters, out var validatedToken);

                if (principal == null || !principal.Identity!.IsAuthenticated)
                {
                    await WriteUnauthorizedAsync(context, "Token validation failed.");
                    return;
                }

                // Attach claims to context for downstream usage  
                context.Items["User"] = principal;

                _logger.LogInformation("Azure AD authentication succeeded for {Name}", principal.Identity.Name);
            }
            catch (SecurityTokenValidationException stvex)
            {
                _logger.LogWarning(stvex, "Token validation failed.");
                await WriteUnauthorizedAsync(context, "Token validation failed.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during token validation.");
                await WriteUnauthorizedAsync(context, "Authentication error.");
                return;
            }

            await next(context);
        }

        /// <summary>  
        /// Writes a 401 Unauthorized response without passing HttpRequestData as a parameter  
        /// to avoid cross-assembly type mismatches.  
        /// </summary>  
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