using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Services
{
    
    /// <summary>  
    /// Implements PostgreSQL persistence for <see cref="EmailNotification"/> using Dapper.  
    /// Provides idempotent operations, transaction support, and distributed processing capabilities.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// <b>Key Features:</b>  
    /// - Idempotent saves using ON CONFLICT DO UPDATE  
    /// - Row-level locking for distributed processing (FOR UPDATE SKIP LOCKED)  
    /// - Transaction-aware updates for atomic operations  
    /// - Comprehensive error handling with specific PostgreSQL error codes  
    /// - Structured logging and telemetry tracking  
    /// </para>  
    /// <para>  
    /// <b>Database Schema:</b> Targets the <c>emailnotifications</c> table in PostgreSQL.  
    /// </para>  
    /// </remarks>  
    public class EmailNotificationRepository : IEmailNotificationRepository
    {
        private readonly IDbConnectionFactory _dbFactory;
        private readonly ILogger<EmailNotificationRepository> _logger;
        private readonly ICustomTelemetry _telemetry;

        /// <summary>  
        /// Initializes a new instance of the <see cref="EmailNotificationRepository"/> class.  
        /// </summary>  
        public EmailNotificationRepository(
            IDbConnectionFactory dbFactory,
            ILogger<EmailNotificationRepository> logger,
            ICustomTelemetry telemetry)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        /// <inheritdoc/>  
        public async Task<SaveNotificationResult> SaveNotificationAsync(
            EmailNotification notification,
            CancellationToken cancellationToken = default)
        {
            // ✅ Input validation  
            ArgumentNullException.ThrowIfNull(notification, nameof(notification));

            if (string.IsNullOrWhiteSpace(notification.NotificationId))
                throw new ArgumentException("NotificationId cannot be empty.", nameof(notification));

            if (string.IsNullOrWhiteSpace(notification.SubscriptionId))
                throw new ArgumentException("SubscriptionId cannot be empty.", nameof(notification));

            if (notification.NotificationDateTime == default)
                throw new ArgumentException("NotificationDateTime must be set.", nameof(notification));

            // ✅ Validate processing status  
            if (!Enum.IsDefined(typeof(NotificationProcessingStatus), notification.ProcessingStatus))
            {
                throw new ArgumentException(
                    $"Invalid ProcessingStatus: {notification.ProcessingStatus}. " +
                    "Must be one of: Pending, Processing, Completed, Failed",
                    nameof(notification));
            }

            // ✅ Column names match database schema (lowercase, no underscores)  
            // Returns whether row was inserted (true) or updated (false)  
            const string sql = @"  
                INSERT INTO emailnotifications (  
                    notificationid,  
                    subscriptionid,  
                    changetype,  
                    resourceuri,  
                    resourceid,  
                    notificationdatetime,  
                    receiveddatetime,  
                    rawnotificationpayload,  
                    processingstatus,  
                    errormessage,  
                    retrycount  
                )  
                VALUES (  
                    @NotificationId,  
                    @SubscriptionId,  
                    @ChangeType,  
                    @ResourceUri,  
                    @ResourceId,  
                    @NotificationDateTime,  
                    @ReceivedDateTime,  
                    @RawNotificationPayload,  
                    @ProcessingStatus,  
                    @ErrorMessage,  
                    @RetryCount  
                )  
                ON CONFLICT (notificationid)   
                DO UPDATE SET  
                    processingstatus = EXCLUDED.processingstatus,  
                    errormessage = EXCLUDED.errormessage,  
                    retrycount = EXCLUDED.retrycount,  
                    receiveddatetime = EXCLUDED.receiveddatetime  
                RETURNING (xmax = 0) AS inserted;";

            try
            {
                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                var command = new CommandDefinition(
                    sql,
                    notification,
                    cancellationToken: cancellationToken,
                    commandTimeout: 30
                );

                var wasInserted = await conn.ExecuteScalarAsync<bool>(command);

                var result = wasInserted
                    ? SaveNotificationResult.Inserted
                    : SaveNotificationResult.Updated;

                _logger.LogInformation(
                    "✅ Email notification saved: {NotificationId}, Result: {Result}, Status: {Status}",
                    notification.NotificationId,
                    result,
                    notification.ProcessingStatus);

                _telemetry.TrackEvent("EmailNotification_Saved", new Dictionary<string, string>
                {
                    { "NotificationId", notification.NotificationId },
                    { "SubscriptionId", notification.SubscriptionId },
                    { "ChangeType", notification.ChangeType },
                    { "Result", result.ToString() },
                    { "ProcessingStatus", notification.ProcessingStatus }
                });

                return result;
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23505") // Unique constraint violation  
            {
                // This should not occur with ON CONFLICT, but handle gracefully  
                _logger.LogWarning(
                    pgEx,
                    "⚠️ Unexpected duplicate notification: {NotificationId}",
                    notification.NotificationId);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveNotificationAsync" },
                    { "ErrorType", "DuplicateKey" },
                    { "NotificationId", notification.NotificationId }
                });

                return SaveNotificationResult.Duplicate;
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23514") // Check constraint violation  
            {
                _logger.LogError(
                    pgEx,
                    "❌ Check constraint violation. ConstraintName: {ConstraintName}, Message: {Message}",
                    pgEx.ConstraintName,
                    pgEx.MessageText);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveNotificationAsync" },
                    { "ErrorType", "CheckConstraintViolation" },
                    { "ConstraintName", pgEx.ConstraintName ?? "Unknown" }
                });

                throw new ArgumentException(
                    $"Invalid data violates database constraint '{pgEx.ConstraintName}': {pgEx.MessageText}",
                    pgEx);
            }
            catch (PostgresException pgEx)
            {
                _logger.LogError(
                    pgEx,
                    "❌ PostgreSQL error while saving notification. " +
                    "SQL State: {SqlState}, Error Code: {ErrorCode}, Message: {Message}",
                    pgEx.SqlState,
                    pgEx.ErrorCode,
                    pgEx.MessageText);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveNotificationAsync" },
                    { "SqlState", pgEx.SqlState ?? "Unknown" },
                    { "ErrorCode", pgEx.ErrorCode.ToString() },
                    { "NotificationId", notification.NotificationId }
                });

                throw new InvalidOperationException(
                    $"Database error while saving notification: {pgEx.MessageText}",
                    pgEx);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "⚠️ Save operation cancelled for NotificationId: {NotificationId}",
                    notification.NotificationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Unexpected error while saving notification: {NotificationId}",
                    notification.NotificationId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "SaveNotificationAsync" },
                    { "NotificationId", notification.NotificationId },
                    { "ExceptionType", ex.GetType().Name }
                });

                throw;
            }
        }

        /// <inheritdoc/>  
        public async Task<IEnumerable<EmailNotification>> GetAndLockPendingNotificationsAsync(
            int batchSize = 10,
            int maxRetryCount = 3,
            CancellationToken cancellationToken = default)
        {
            if (batchSize <= 0 || batchSize > 100)
                throw new ArgumentOutOfRangeException(
                    nameof(batchSize),
                    "Batch size must be between 1 and 100");

            if (maxRetryCount < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxRetryCount),
                    "Max retry count cannot be negative");

            // ✅ FOR UPDATE SKIP LOCKED prevents race conditions in distributed processing  
            const string sql = @"  
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
                WHERE processingstatus IN ('Pending', 'Failed')  
                  AND retrycount < @MaxRetryCount  
                ORDER BY receiveddatetime ASC  
                LIMIT @BatchSize  
                FOR UPDATE SKIP LOCKED;";

            try
            {
                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                var command = new CommandDefinition(
                    sql,
                    new { BatchSize = batchSize, MaxRetryCount = maxRetryCount },
                    cancellationToken: cancellationToken,
                    commandTimeout: 30
                );

                var notifications = await conn.QueryAsync<EmailNotification>(command);
                var notificationList = notifications.AsList();

                _logger.LogInformation(
                    "📋 Retrieved and locked {Count} pending notifications for processing",
                    notificationList.Count);

                _telemetry.TrackEvent("EmailNotification_Fetched", new Dictionary<string, string>
                {
                    { "Count", notificationList.Count.ToString() },
                    { "BatchSize", batchSize.ToString() },
                    { "MaxRetryCount", maxRetryCount.ToString() }
                });

                return notificationList;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Error retrieving and locking pending notifications");

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "GetAndLockPendingNotificationsAsync" },
                    { "BatchSize", batchSize.ToString() }
                });

                throw;
            }
        }

        /// <inheritdoc/>  
        public async Task UpdateNotificationStatusAsync(
            string notificationId,
            NotificationProcessingStatus status,
            string? errorMessage = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(notificationId, nameof(notificationId));

            var statusString = status.ToString();

            // ✅ Only increment retry count for failed status  
            const string sql = @"  
                UPDATE emailnotifications  
                SET   
                    processingstatus = @Status,  
                    errormessage = @ErrorMessage,  
                    retrycount = CASE   
                        WHEN @Status = 'Failed' THEN retrycount + 1   
                        ELSE retrycount   
                    END,  
                    receiveddatetime = CURRENT_TIMESTAMP  
                WHERE notificationid = @NotificationId;";

            try
            {
                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                var command = new CommandDefinition(
                    sql,
                    new
                    {
                        NotificationId = notificationId,
                        Status = statusString,
                        ErrorMessage = errorMessage
                    },
                    cancellationToken: cancellationToken,
                    commandTimeout: 30
                );

                var rowsAffected = await conn.ExecuteAsync(command);

                if (rowsAffected == 0)
                {
                    _logger.LogWarning(
                        "⚠️ Notification not found for update: {NotificationId}",
                        notificationId);

                    throw new InvalidOperationException(
                        $"Notification '{notificationId}' not found in database.");
                }

                _logger.LogInformation(
                    "✅ Notification status updated: {NotificationId} -> {Status}" +
                    (errorMessage != null ? ", Error: {ErrorMessage}" : string.Empty),
                    notificationId,
                    statusString,
                    errorMessage);

                _telemetry.TrackEvent("EmailNotification_StatusUpdated", new Dictionary<string, string>
                {
                    { "NotificationId", notificationId },
                    { "NewStatus", statusString },
                    { "HasError", (errorMessage != null).ToString() }
                });
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23514") // Check constraint violation  
            {
                _logger.LogError(
                    pgEx,
                    "❌ Invalid status value: {Status}. " +
                    "Must be one of: Pending, Processing, Completed, Failed",
                    statusString);

                throw new ArgumentException(
                    $"Invalid status '{statusString}'. " +
                    "Must be one of: Pending, Processing, Completed, Failed",
                    nameof(status),
                    pgEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Error updating notification status: {NotificationId}",
                    notificationId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "UpdateNotificationStatusAsync" },
                    { "NotificationId", notificationId },
                    { "Status", statusString }
                });

                throw;
            }
        }

        /// <inheritdoc/>  
        public async Task UpdateNotificationStatusInTransactionAsync(
            string notificationId,
            NotificationProcessingStatus status,
            IDbTransaction transaction,
            string? errorMessage = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(notificationId, nameof(notificationId));
            ArgumentNullException.ThrowIfNull(transaction, nameof(transaction));

            var statusString = status.ToString();

            const string sql = @"  
                UPDATE emailnotifications  
                SET   
                    processingstatus = @Status,  
                    errormessage = @ErrorMessage,  
                    retrycount = CASE   
                        WHEN @Status = 'Failed' THEN retrycount + 1   
                        ELSE retrycount   
                    END,  
                    receiveddatetime = CURRENT_TIMESTAMP  
                WHERE notificationid = @NotificationId;";

            try
            {
                var command = new CommandDefinition(
                    sql,
                    new
                    {
                        NotificationId = notificationId,
                        Status = statusString,
                        ErrorMessage = errorMessage
                    },
                    transaction: transaction,
                    cancellationToken: cancellationToken,
                    commandTimeout: 30
                );

                var rowsAffected = await transaction.Connection!.ExecuteAsync(command);

                if (rowsAffected == 0)
                {
                    throw new InvalidOperationException(
                        $"Notification '{notificationId}' not found in database.");
                }

                _logger.LogDebug(
                    "✅ Notification status updated in transaction: {NotificationId} -> {Status}",
                    notificationId,
                    statusString);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Error updating notification status in transaction: {NotificationId}",
                    notificationId);

                throw;
            }
        }

        /// <inheritdoc/>  
        public async Task<EmailNotification?> GetNotificationByIdAsync(
            string notificationId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(notificationId, nameof(notificationId));

            const string sql = @"  
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
                WHERE notificationid = @NotificationId;";

            try
            {
                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                var command = new CommandDefinition(
                    sql,
                    new { NotificationId = notificationId },
                    cancellationToken: cancellationToken,
                    commandTimeout: 30
                );

                var notification = await conn.QuerySingleOrDefaultAsync<EmailNotification>(command);

                if (notification == null)
                {
                    _logger.LogDebug(
                        "📭 Notification not found: {NotificationId}",
                        notificationId);
                }
                else
                {
                    _logger.LogDebug(
                        "📬 Notification retrieved: {NotificationId}, Status: {Status}",
                        notificationId,
                        notification.ProcessingStatus);
                }

                return notification;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Error retrieving notification: {NotificationId}",
                    notificationId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "GetNotificationByIdAsync" },
                    { "NotificationId", notificationId }
                });

                throw;
            }
        }

        /// <inheritdoc/>  
        public async Task<IEnumerable<EmailNotification>> GetNotificationsBySubscriptionAsync(
            string subscriptionId,
            int limit = 100,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId, nameof(subscriptionId));

            if (limit <= 0 || limit > 1000)
                throw new ArgumentOutOfRangeException(
                    nameof(limit),
                    "Limit must be between 1 and 1000");

            const string sql = @"  
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
                WHERE subscriptionid = @SubscriptionId  
                ORDER BY notificationdatetime DESC  
                LIMIT @Limit;";

            try
            {
                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                var command = new CommandDefinition(
                    sql,
                    new { SubscriptionId = subscriptionId, Limit = limit },
                    cancellationToken: cancellationToken,
                    commandTimeout: 30
                );

                var notifications = await conn.QueryAsync<EmailNotification>(command);
                var notificationList = notifications.AsList();

                _logger.LogInformation(
                    "📋 Retrieved {Count} notifications for subscription: {SubscriptionId}",
                    notificationList.Count,
                    subscriptionId);

                return notificationList;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Error retrieving notifications for subscription: {SubscriptionId}",
                    subscriptionId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "GetNotificationsBySubscriptionAsync" },
                    { "SubscriptionId", subscriptionId }
                });

                throw;
            }
        }
    }
}