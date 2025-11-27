using System;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace MailNotificationFunctionApp.Middleware
{
    /// <summary>  
    /// Ensures every request has a correlation ID for traceability.  
    /// Adds one if missing.  
    /// </summary>  
    public class CorrelationIdMiddleware : IFunctionsWorkerMiddleware
    {
        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var req = await context.GetHttpRequestDataAsync();
            if (req != null)
            {
                if (!req.Headers.TryGetValues("x-correlation-id", out var values))
                {
                    req.Headers.Add("x-correlation-id", Guid.NewGuid().ToString());
                }
            }
            await next(context);
        }
    }
}