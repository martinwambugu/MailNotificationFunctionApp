using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MailNotificationFunctionApp.Middleware
{
    public static class MiddlewareExtensions
    {
        public static IServiceCollection AddCustomMiddlewares(
            this IServiceCollection services, IConfiguration config)
        {
            // Always-on middlewares  
            services.AddSingleton<FunctionExceptionMiddleware>();
            services.AddSingleton<CorrelationIdMiddleware>();
            services.AddSingleton<OperationNameMiddleware>();

            // Auth mode switch  
            var authMode = config["Auth:Mode"]?.ToLowerInvariant();

            if (authMode == "apikey")
            {
                services.AddSingleton<ApiKeyAuthMiddleware>();
            }
            else if (authMode == "azuread")
            {
                services.AddSingleton<AzureAdAuthMiddleware>();
            }

            return services;
        }

        public static void UseCustomMiddlewares(
            this IFunctionsWorkerApplicationBuilder builder, IConfiguration config)
        {
            // Always-on middlewares  
            builder.UseMiddleware<FunctionExceptionMiddleware>();
            builder.UseMiddleware<CorrelationIdMiddleware>();
            builder.UseMiddleware<OperationNameMiddleware>();

            // Auth mode switch  
            var authMode = config["Auth:Mode"]?.ToLowerInvariant();

            if (authMode == "apikey")
            {
                builder.UseMiddleware<ApiKeyAuthMiddleware>();
            }
            else if (authMode == "azuread")
            {
                builder.UseMiddleware<AzureAdAuthMiddleware>();
            }

        }
    }
}