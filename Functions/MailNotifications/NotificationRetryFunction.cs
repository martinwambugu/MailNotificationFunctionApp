using MailNotificationFunctionApp.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Functions.MailNotifications
{
    /// <summary>  
    /// Timer-triggered function to retry failed notifications.  
    /// Runs every 5 minutes to process notifications that failed during initial processing.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This function retrieves notifications with status 'Failed' and retry_count less than 10,  
    /// then attempts to reprocess them using the <see cref="NotificationRetryService"/>.  
    /// </para>  
    /// <para>  
    /// <b>Schedule:</b> Runs every 5 minutes using CRON expression "0 */5 * * * *"  
    /// </para>  
    /// <para>  
    /// <b>Performance:</b> Processes up to 100 failed notifications per execution to prevent overload.  
    /// </para>  
    /// </remarks>  
    public class NotificationRetryFunction
    {
        private readonly NotificationRetryService _retryService;
        private readonly ILogger<NotificationRetryFunction> _logger;

        /// <summary>  
        /// Initializes a new instance of the <see cref="NotificationRetryFunction"/> class.  
        /// </summary>  
        public NotificationRetryFunction(
            NotificationRetryService retryService,
            ILogger<NotificationRetryFunction> logger)
        {
            _retryService = retryService ?? throw new ArgumentNullException(nameof(retryService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>  
        /// Timer-triggered function that runs every 5 minutes to retry failed notifications.  
        /// </summary>  
        /// <param name="timer">Timer schedule information.</param>  
        /// <remarks>  
        /// CRON Expression: "0 */5 * * * *"  
        /// - Runs at minute 0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55 of every hour  
        /// </remarks>  
        [Function("NotificationRetry")]
        public async Task Run(
            [TimerTrigger("0 */5 * * * *")] TimerInfo timer)
        {
            _logger.LogInformation(
                "⏰ Notification retry job started at {Time}. Next run: {NextRun}",
                DateTime.UtcNow,
                timer.ScheduleStatus?.Next);

            try
            {
                var retriedCount = await _retryService.RetryFailedNotificationsAsync();

                _logger.LogInformation(
                    "✅ Notification retry job completed successfully. Retried: {Count}, Duration: {Duration}ms",
                    retriedCount,
                    timer.ScheduleStatus?.Last);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in notification retry job");
                // Don't rethrow - let the function complete so the timer continues  
            }
        }
    }
}