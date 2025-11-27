using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace MailNotificationFunctionApp.Middleware
{
    /// <summary>  
    /// Global exception handler middleware for Azure Functions Isolated Worker.  
    /// Catches unhandled exceptions and returns a standardized error response.  
    /// </summary>  
    public class FunctionExceptionMiddleware : IFunctionsWorkerMiddleware
    {
        private readonly ILogger<FunctionExceptionMiddleware> _logger;

        public FunctionExceptionMiddleware(ILogger<FunctionExceptionMiddleware> logger)
        {
            _logger = logger;
        }

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in {FunctionName}", context.FunctionDefinition.Name);

                var req = await context.GetHttpRequestDataAsync();
                if (req != null)
                {
                    var res = req.CreateResponse(HttpStatusCode.InternalServerError);
                    await res.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
                    context.GetInvocationResult().Value = res;
                }
            }
        }
    }
}