using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Middleware
{
    /// <summary>  
    /// Logs the operation (function) name for telemetry purposes.  
    /// </summary>  
    public class OperationNameMiddleware : IFunctionsWorkerMiddleware
    {
        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var operationName = context.FunctionDefinition.Name;

            var logger = context.InstanceServices.GetRequiredService<ILogger<OperationNameMiddleware>>();
            logger.LogInformation("Executing operation {OperationName}", operationName);

            await next(context);
        }
    }
}