using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Functions.MailNotifications
{
    /// <summary>  
    /// Timer-triggered function to retry failed notifications with distributed locking.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This function retrieves notifications with status 'Failed' and retrycount less than configured maximum,  
    /// then attempts to reprocess them using the <see cref="NotificationRetryService"/>.  
    /// </para>  
    /// <para>  
    /// <b>Schedule:</b> Runs every 5 minutes using CRON expression "0 */5 * * * *"  
    /// </para>  
    /// <para>  
    /// <b>Key Features:</b>  
    /// - Distributed locking prevents duplicate processing by multiple instances  
    /// - Configurable batch size and concurrency  
    /// - Graceful shutdown support via cancellation tokens  
    /// - Comprehensive telemetry and error tracking  
    /// </para>  
    /// <para>  
    /// <b>Configuration:</b>  
    /// <code>  
    /// {  
    ///   "NotificationRetryJob": {  
    ///     "MaxRetries": 100,  
    ///     "BatchSize": 5,  
    ///     "Enabled": true  
    ///   }  
    /// }  
    /// </code>  
    /// </para>  
    /// </remarks>  
    public class NotificationRetryFunction
    {
        private readonly NotificationRetryService _retryService;
        private readonly ILogger<NotificationRetryFunction> _logger;
        private readonly ICustomTelemetry _telemetry;
        private readonly IConfiguration _config;
        private readonly int _maxRetries;
        private readonly int _batchSize;
        private readonly bool _enabled;

        /// <summary>  
        /// Initializes a new instance of the <see cref="NotificationRetryFunction"/> class.  
        /// </summary>  
        public NotificationRetryFunction(
            NotificationRetryService retryService,
            IConfiguration config,
            ILogger<NotificationRetryFunction> logger,
            ICustomTelemetry telemetry)
        {
            _retryService = retryService ?? throw new ArgumentNullException(nameof(retryService));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

            // ✅ Load configuration with defaults  
            _maxRetries = _config.GetValue<int>("NotificationRetryJob:MaxRetries", 100);
            _batchSize = _config.GetValue<int>("NotificationRetryJob:BatchSize", 5);
            _enabled = _config.GetValue<bool>("NotificationRetryJob:Enabled", true);

            _logger.LogInformation(
                "📋 NotificationRetryFunction initialized. MaxRetries: {MaxRetries}, BatchSize: {BatchSize}, Enabled: {Enabled}",
                _maxRetries,
                _batchSize,
                _enabled);
        }

        /// <summary>  
        /// Timer-triggered function that runs every 5 minutes to retry failed notifications.  
        /// </summary>  
        /// <param name="timer">Timer schedule information.</param>  
        /// <param name="cancellationToken">Cancellation token for graceful shutdown.</param>  
        /// <remarks>  
        /// <para>  
        /// <b>CRON Expression:</b> "0 */5 * * * *"  
        /// - Runs at minute 0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55 of every hour  
        /// </para>  
        /// <para>  
        /// <b>Graceful Shutdown:</b> Respects cancellation token to allow in-flight operations  
        /// to complete before Azure Functions host shutdown.  
        /// </para>  
        /// </remarks>  
        [Function("NotificationRetry")]
        public async Task Run(
            [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
            CancellationToken cancellationToken) // ✅ ADD cancellation token  
        {
            var executionId = Guid.NewGuid().ToString("N");
            var stopwatch = Stopwatch.StartNew();

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["ExecutionId"] = executionId,
                ["FunctionName"] = nameof(NotificationRetryFunction)
            }))
            {
                _logger.LogInformation(
                    "⏰ Notification retry job started. ExecutionId: {ExecutionId}, Time: {Time:yyyy-MM-dd HH:mm:ss} UTC",
                    executionId,
                    DateTime.UtcNow);

                _telemetry.TrackEvent("NotificationRetryJob_Started", new Dictionary<string, string>
                {
                    { "ExecutionId", executionId },
                    { "ScheduledTime", DateTime.UtcNow.ToString("o") },
                    { "NextScheduledRun", timer.ScheduleStatus?.Next.ToString("o") ?? "unknown" }
                });

                // ✅ Check if job is enabled  
                if (!_enabled)
                {
                    _logger.LogWarning(
                        "⚠️ Notification retry job is disabled. Skipping execution. ExecutionId: {ExecutionId}",
                        executionId);

                    _telemetry.TrackEvent("NotificationRetryJob_Skipped", new Dictionary<string, string>
                    {
                        { "ExecutionId", executionId },
                        { "Reason", "JobDisabled" }
                    });

                    return;
                }

                // ✅ Check for cancellation before starting  
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "⚠️ Notification retry job cancelled before start. ExecutionId: {ExecutionId}",
                        executionId);
                    return;
                }

                try
                {
                    // ✅ FIXED: RetryFailedNotificationsAsync now returns RetryResult, not int  
                    var result = await _retryService.RetryFailedNotificationsAsync(
                        maxRetries: _maxRetries,
                        batchSize: _batchSize,
                        cancellationToken: cancellationToken);

                    stopwatch.Stop();

                    _logger.LogInformation(
                        "✅ Notification retry job completed successfully. " +
                        "ExecutionId: {ExecutionId}, Total: {Total}, Success: {Success}, Failed: {Failed}, Duration: {Duration}ms",
                        executionId,
                        result.TotalRetried,
                        result.SuccessCount,
                        result.FailureCount,
                        stopwatch.ElapsedMilliseconds);

                    // ✅ Track detailed metrics  
                    _telemetry.TrackEvent("NotificationRetryJob_Completed", new Dictionary<string, string>
                    {
                        { "ExecutionId", executionId },
                        { "TotalRetried", result.TotalRetried.ToString() },
                        { "SuccessCount", result.SuccessCount.ToString() },
                        { "FailureCount", result.FailureCount.ToString() },
                        { "DurationMs", stopwatch.ElapsedMilliseconds.ToString() },
                        { "Status", "Success" }
                    });

                    _telemetry.TrackMetric("NotificationRetryJob_Duration", stopwatch.ElapsedMilliseconds, new Dictionary<string, string>
                    {
                        { "ExecutionId", executionId }
                    });

                    _telemetry.TrackMetric("NotificationRetryJob_TotalProcessed", result.TotalRetried, new Dictionary<string, string>
                    {
                        { "ExecutionId", executionId }
                    });

                    _telemetry.TrackMetric("NotificationRetryJob_SuccessRate",
                        result.TotalRetried > 0 ? (double)result.SuccessCount / result.TotalRetried * 100 : 100,
                        new Dictionary<string, string>
                        {
                            { "ExecutionId", executionId }
                        });
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();

                    _logger.LogWarning(
                        "⚠️ Notification retry job was cancelled. ExecutionId: {ExecutionId}, Duration: {Duration}ms",
                        executionId,
                        stopwatch.ElapsedMilliseconds);

                    _telemetry.TrackEvent("NotificationRetryJob_Cancelled", new Dictionary<string, string>
                    {
                        { "ExecutionId", executionId },
                        { "DurationMs", stopwatch.ElapsedMilliseconds.ToString() }
                    });

                    // ✅ Don't rethrow - let function complete gracefully  
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();

                    _logger.LogError(
                        ex,
                        "❌ Error in notification retry job. ExecutionId: {ExecutionId}, Duration: {Duration}ms, Error: {ErrorMessage}",
                        executionId,
                        stopwatch.ElapsedMilliseconds,
                        ex.Message);

                    _telemetry.TrackException(ex, new Dictionary<string, string>
                    {
                        { "ExecutionId", executionId },
                        { "FunctionName", nameof(NotificationRetryFunction) },
                        { "DurationMs", stopwatch.ElapsedMilliseconds.ToString() },
                        { "ErrorType", ex.GetType().Name }
                    });

                    _telemetry.TrackEvent("NotificationRetryJob_Failed", new Dictionary<string, string>
                    {
                        { "ExecutionId", executionId },
                        { "ErrorMessage", ex.Message },
                        { "ErrorType", ex.GetType().Name },
                        { "DurationMs", stopwatch.ElapsedMilliseconds.ToString() }
                    });

                    // ✅ Don't rethrow - let the timer continue running  
                }
                finally
                {
                    _logger.LogInformation(
                        "🏁 Notification retry job execution ended. ExecutionId: {ExecutionId}, NextRun: {NextRun:yyyy-MM-dd HH:mm:ss} UTC",
                        executionId,
                        timer.ScheduleStatus?.Next ?? DateTime.MinValue);
                }
            }
        }
    }
}