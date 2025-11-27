using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using MailNotificationFunctionApp.Interfaces;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Infrastructure
{
    /// <summary>  
    /// Base class for Azure Functions to provide consistent logging, telemetry,  
    /// and reusable HTTP response helpers.  
    /// </summary>  
    public abstract class BaseFunction
    {
        protected readonly ILogger _logger;
        protected readonly ICustomTelemetry _telemetry;

        protected BaseFunction(ILogger logger, ICustomTelemetry telemetry)
        {
            _logger = logger;
            _telemetry = telemetry;
        }

        #region Logging Helpers  

        /// <summary>  
        /// Logs the start of a function execution.  
        /// </summary>  
        /// <param name="functionName">The function name.</param>  
        /// <param name="parameters">Optional parameters for logging.</param>  
        protected void LogStart(string functionName, object parameters = null)
        {
            _logger.LogInformation("▶️ Starting {FunctionName} with params {@Params}", functionName, parameters);
            _telemetry.TrackEvent($"{functionName}_Start");
        }

        /// <summary>  
        /// Logs the end of a function execution.  
        /// </summary>  
        /// <param name="functionName">The function name.</param>  
        /// <param name="result">Optional result for logging.</param>  
        protected void LogEnd(string functionName, object result = null)
        {
            _logger.LogInformation("⏹ Finished {FunctionName} with result {@Result}", functionName, result);
            _telemetry.TrackEvent($"{functionName}_End");
        }

        #endregion

        #region HTTP Response Helpers  

        /// <summary>  
        /// Returns a 400 Bad Request HTTP response with a JSON error message.  
        /// </summary>  
        protected async Task<HttpResponseData> BadRequest(HttpRequestData req, string message)
        {
            var resp = req.CreateResponse(HttpStatusCode.BadRequest);
            await resp.WriteAsJsonAsync(new { error = message });
            return resp;
        }

        /// <summary>  
        /// Returns a 404 Not Found HTTP response with a JSON error message.  
        /// </summary>  
        protected async Task<HttpResponseData> NotFound(HttpRequestData req, string message)
        {
            var resp = req.CreateResponse(HttpStatusCode.NotFound);
            await resp.WriteAsJsonAsync(new { error = message });
            return resp;
        }

        /// <summary>  
        /// Returns a 403 Forbidden HTTP response with a JSON error message.  
        /// </summary>  
        protected async Task<HttpResponseData> Forbidden(HttpRequestData req, string message)
        {
            var resp = req.CreateResponse(HttpStatusCode.Forbidden);
            await resp.WriteAsJsonAsync(new { error = message });
            return resp;
        }

        /// <summary>  
        /// Returns a 409 Conflict HTTP response with a JSON error message.  
        /// </summary>  
        protected async Task<HttpResponseData> Conflict(HttpRequestData req, string message)
        {
            var resp = req.CreateResponse(HttpStatusCode.Conflict);
            await resp.WriteAsJsonAsync(new { error = message });
            return resp;
        }

        /// <summary>  
        /// Returns a 401 Unauthorized HTTP response with a JSON error message.  
        /// </summary>  
        protected async Task<HttpResponseData> Unauthorized(HttpRequestData req, string message)
        {
            var resp = req.CreateResponse(HttpStatusCode.Unauthorized);
            await resp.WriteAsJsonAsync(new { error = message });
            return resp;
        }

        /// <summary>  
        /// Returns a 500 Internal Server Error HTTP response with a JSON error message.  
        /// </summary>  
        protected async Task<HttpResponseData> InternalServerError(HttpRequestData req, string message)
        {
            var resp = req.CreateResponse(HttpStatusCode.InternalServerError);
            await resp.WriteAsJsonAsync(new { error = message });
            return resp;
        }

        #endregion
    }
}