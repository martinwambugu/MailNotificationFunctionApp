using Dapper;
using MailNotificationFunctionApp.Extensions;
using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql; // ✅ ADD THIS - needed for BeginTransactionAsync  
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Services
{
    /// <summary>  
    /// Handles retry logic for failed email notifications with distributed locking.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// <b>Key Features:</b>  
    /// - Row-level locking prevents duplicate processing by multiple workers  
    /// - Parallel processing with configurable concurrency  
    /// - Circuit breaker pattern to prevent cascading failures  
    /// - Comprehensive logging and telemetry  
    /// </para>  
    /// <para>  
    /// <b>Retry Policy:</b>  
    /// - Maximum retry count: Configurable (default: 10)  
    /// - Time window: Configurable (default: 24 hours)  
    /// - Batch size: Configurable (default: 100, max: 100 per call)  
    /// - Concurrency: Configurable (default: 5 parallel operations)  
    /// </para>  
    /// </remarks>  
    public class NotificationRetryService
    {
        private readonly IDbConnectionFactory _dbFactory;
        private readonly IEmailNotificationRepository _repository;
        private readonly INotificationHandler _handler;
        private readonly ILogger<NotificationRetryService> _logger;
        private readonly ICustomTelemetry _telemetry;
        private readonly int _maxRetryCount;
        private readonly int _timeWindowHours;

        /// <summary>  
        /// Initializes a new instance of the <see cref="NotificationRetryService"/> class.  
        /// </summary>  
        public NotificationRetryService(
            IDbConnectionFactory dbFactory,
            IEmailNotificationRepository repository,
            INotificationHandler handler,
            IConfiguration config,
            ILogger<NotificationRetryService> logger,
            ICustomTelemetry telemetry)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

            _maxRetryCount = config?.GetValue<int>("NotificationRetry:MaxRetryCount", 10) ?? 10;
            _timeWindowHours = config?.GetValue<int>("NotificationRetry:TimeWindowHours", 24) ?? 24;
        }

        /// <summary>  
        /// Retrieves and retries failed notifications with distributed locking.  
        /// </summary>  
        /// <param name="maxRetries">Maximum number of notifications to retry (default: 100, max: 1000).</param>  
        /// <param name="batchSize">Number of notifications to process in parallel (default: 5, max: 20).</param>  
        /// <param name="cancellationToken">Cancellation token.</param>  
        /// <returns>Result containing retry statistics.</returns>  
        /// <exception cref="ArgumentOutOfRangeException">Thrown when parameters are out of valid range.</exception>  
        public async Task<RetryResult> RetryFailedNotificationsAsync(
            int maxRetries = 100,
            int batchSize = 5,
            CancellationToken cancellationToken = default)
        {
            // ✅ Validate parameters  
            if (maxRetries <= 0 || maxRetries > 1000)
                throw new ArgumentOutOfRangeException(nameof(maxRetries), "Must be between 1 and 1000");

            if (batchSize <= 0 || batchSize > 20)
                throw new ArgumentOutOfRangeException(nameof(batchSize), "Must be between 1 and 20");

            // ✅ Use row-level locking to prevent duplicate processing  
            var sql = $@"  
                SELECT   
                    notificationid AS NotificationId,  
                    subscriptionid AS SubscriptionId,  
                    changetype AS ChangeType,  
                    resourceuri AS ResourceUri,  
                    resourceid AS ResourceId,  
                    notificationdatetime AS NotificationDateTime,  
                    receiveddatetime AS ReceivedDateTime,  
                    rawnotificationpayload AS RawNotificationPayload,  
                    processingstatus AS ProcessingStatus,  
                    errormessage AS ErrorMessage,  
                    retrycount AS RetryCount  
                FROM emailnotifications  
                WHERE processingstatus = 'Failed'  
                  AND retrycount < @MaxRetryCount  
                  AND receiveddatetime > NOW() - INTERVAL '{_timeWindowHours} hours'  
                ORDER BY receiveddatetime ASC  
                LIMIT @Limit  
                FOR UPDATE SKIP LOCKED;";

            try
            {
                _logger.LogInformation(
                    "🔍 Starting retry process. MaxRetries: {MaxRetries}, BatchSize: {BatchSize}, " +
                    "MaxRetryCount: {MaxRetryCount}, TimeWindow: {TimeWindow}h",
                    maxRetries,
                    batchSize,
                    _maxRetryCount,
                    _timeWindowHours);

                var result = new RetryResult();

                // ✅ FIX: Cast to NpgsqlConnection to access BeginTransactionAsync  
                var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);
                if (conn is not NpgsqlConnection npgsqlConn)
                {
                    throw new InvalidOperationException(
                        "Database connection is not a NpgsqlConnection. " +
                        "This service requires PostgreSQL.");
                }

                using (conn)
                using (var transaction = await npgsqlConn.BeginTransactionAsync(cancellationToken))
                {
                    try
                    {
                        var failedNotifications = await npgsqlConn.QueryAsync<EmailNotification>(
                            new CommandDefinition(
                                sql,
                                new { MaxRetryCount = _maxRetryCount, Limit = Math.Min(maxRetries, 100) },
                                transaction: transaction,
                                cancellationToken: cancellationToken));

                        var notificationList = failedNotifications?.ToList() ?? new List<EmailNotification>();

                        if (notificationList.Count == 0)
                        {
                            _logger.LogInformation("✅ No failed notifications found to retry");
                            await transaction.RollbackAsync(cancellationToken);
                            return result;
                        }

                        _logger.LogInformation(
                            "📋 Found {Count} failed notifications eligible for retry (locked for processing)",
                            notificationList.Count);

                        // ✅ FIX: Process sequentially first, then use Task.WhenAll  
                        var semaphore = new SemaphoreSlim(batchSize);
                        var consecutiveFailures = 0;
                        const int maxConsecutiveFailures = 10;

                        var tasks = new List<Task<bool>>();

                        foreach (var notification in notificationList)
                        {
                            // ✅ Circuit breaker check (before creating task)  
                            if (consecutiveFailures >= maxConsecutiveFailures)
                            {
                                _logger.LogWarning(
                                    "⚠️ Circuit breaker active. Skipping remaining notifications.");
                                break;
                            }

                            // ✅ Create task for parallel processing  
                            var task = Task.Run(async () =>
                            {
                                await semaphore.WaitAsync(cancellationToken);
                                try
                                {
                                    var success = await RetryNotificationAsync(notification, cancellationToken);

                                    if (!success)
                                    {
                                        Interlocked.Increment(ref consecutiveFailures);
                                    }
                                    else
                                    {
                                        Interlocked.Exchange(ref consecutiveFailures, 0);
                                    }

                                    return success;
                                }
                                finally
                                {
                                    semaphore.Release();
                                }
                            }, cancellationToken);

                            tasks.Add(task);
                        }

                        var results = await Task.WhenAll(tasks);

                        result.TotalRetried = notificationList.Count;
                        result.SuccessCount = results.Count(r => r);
                        result.FailureCount = results.Count(r => !r);

                        await transaction.CommitAsync(cancellationToken);

                        _logger.LogInformation(
                            "✅ Retry process completed. Total: {Total}, Success: {Success}, Failed: {Failed}",
                            result.TotalRetried,
                            result.SuccessCount,
                            result.FailureCount);

                        _telemetry.TrackMetric("MailNotifications_Retried_Success", result.SuccessCount);
                        _telemetry.TrackMetric("MailNotifications_Retried_Failed", result.FailureCount);
                        _telemetry.TrackMetric("MailNotifications_Retried_Total", result.TotalRetried);

                        return result;
                    }
                    catch
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during retry process");
                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "RetryFailedNotificationsAsync" }
                });
                throw;
            }
        }

        /// <summary>  
        /// Retries a single notification.  
        /// </summary>  
        private async Task<bool> RetryNotificationAsync(
            EmailNotification notification,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation(
                    "🔄 Retrying notification {NotificationId} (attempt {RetryCount}/{MaxRetry})",
                    notification.NotificationId,
                    notification.RetryCount + 1,
                    _maxRetryCount);

                // ✅ Validate raw payload exists  
                if (string.IsNullOrWhiteSpace(notification.RawNotificationPayload))
                {
                    _logger.LogWarning(
                        "⚠️ Notification {NotificationId} has empty payload, marking as permanently failed",
                        notification.NotificationId);

                    await MarkAsPermanentlyFailedAsync(
                        notification.NotificationId,
                        "Empty payload",
                        cancellationToken);

                    return false;
                }

                // ✅ CLEANER: Use extension method to reconstruct NotificationItem  
                var notificationItem = notification.ToNotificationItem();

                // ✅ Attempt to reprocess the notification  
                var success = await _handler.HandleAsync(
                    notificationItem,
                    notification.RawNotificationPayload,
                    cancellationToken);

                if (success)
                {
                    _logger.LogInformation(
                        "✅ Notification {NotificationId} retried successfully",
                        notification.NotificationId);

                    _telemetry.TrackEvent("EmailNotification_RetrySuccess", new Dictionary<string, string>
                    {
                        { "NotificationId", notification.NotificationId },
                        { "RetryCount", notification.RetryCount.ToString() }
                    });

                    return true;
                }
                else
                {
                    _logger.LogWarning(
                        "⚠️ Notification {NotificationId} retry returned false",
                        notification.NotificationId);

                    _telemetry.TrackEvent("EmailNotification_RetryFailed", new Dictionary<string, string>
                    {
                        { "NotificationId", notification.NotificationId },
                        { "RetryCount", notification.RetryCount.ToString() },
                        { "Reason", "Handler returned false" }
                    });

                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Retry failed for notification {NotificationId}",
                    notification.NotificationId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "NotificationId", notification.NotificationId },
                    { "RetryCount", notification.RetryCount.ToString() },
                    { "Operation", "RetryNotificationAsync" }
                });

                return false;
            }
        }

        /// <summary>  
        /// Marks a notification as permanently failed.  
        /// </summary>  
        private async Task MarkAsPermanentlyFailedAsync(
            string notificationId,
            string errorMessage,
            CancellationToken cancellationToken)
        {
            try
            {
                // ✅ FIX: Cast to NpgsqlConnection  
                var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);
                if (conn is not NpgsqlConnection npgsqlConn)
                {
                    throw new InvalidOperationException(
                        "Database connection is not a NpgsqlConnection.");
                }

                using (conn)
                using (var transaction = await npgsqlConn.BeginTransactionAsync(cancellationToken))
                {
                    try
                    {
                        await _repository.UpdateNotificationStatusInTransactionAsync(
                            notificationId,
                            NotificationProcessingStatus.Failed,
                            transaction,
                            errorMessage,
                            cancellationToken);

                        await transaction.CommitAsync(cancellationToken);

                        _logger.LogInformation(
                            "📝 Marked notification {NotificationId} as permanently failed: {ErrorMessage}",
                            notificationId,
                            errorMessage);
                    }
                    catch
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Failed to mark notification {NotificationId} as permanently failed",
                    notificationId);
                // Don't throw - this is a best-effort update  
            }
        }
    }

    /// <summary>  
    /// Result of a retry operation.  
    /// </summary>  
    public class RetryResult
    {
        /// <summary>Total number of notifications attempted to retry.</summary>  
        public int TotalRetried { get; set; }

        /// <summary>Number of notifications successfully processed.</summary>  
        public int SuccessCount { get; set; }

        /// <summary>Number of notifications that failed during retry.</summary>  
        public int FailureCount { get; set; }
    }
}