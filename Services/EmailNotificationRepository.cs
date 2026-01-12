using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace MailNotificationFunctionApp.Services
{
    /// <summary>  
    /// Implements PostgreSQL persistence for <see cref="EmailNotification"/> using Dapper.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This repository handles database interactions for email notification records.  
    /// It uses parameterized queries to prevent SQL injection and includes comprehensive error handling.  
    /// </para>  
    /// <para>  
    /// <b>Database Schema:</b> Targets the <c>email_notifications</c> table in PostgreSQL.  
    /// </para>  
    /// <para>  
    /// <b>Idempotency:</b> Duplicate notifications (based on notification_id) are handled gracefully  
    /// using ON CONFLICT DO UPDATE logic, making this method safe to call multiple times with the same data.  
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
        public async Task SaveNotificationAsync(
            EmailNotification notification,
            CancellationToken cancellationToken = default)
        {
            // ✅ Input validation  
            ArgumentNullException.ThrowIfNull(notification);

            if (string.IsNullOrWhiteSpace(notification.NotificationId))
                throw new ArgumentException("NotificationId cannot be empty.", nameof(notification));

            if (string.IsNullOrWhiteSpace(notification.SubscriptionId))
                throw new ArgumentException("SubscriptionId cannot be empty.", nameof(notification));

            if (notification.NotificationDateTime == default)
                throw new ArgumentException("NotificationDateTime must be set.", nameof(notification));

            // ✅ Use snake_case column names (PostgreSQL convention)  
            const string sql = @"  
                INSERT INTO email_notifications (  
                    notification_id,  
                    subscription_id,  
                    change_type,  
                    resource_uri,  
                    resource_id,  
                    notification_date_time,  
                    received_date_time,  
                    raw_notification_payload,  
                    processing_status,  
                    error_message,  
                    retry_count  
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
                ON CONFLICT (notification_id)   
                DO UPDATE SET  
                    processing_status = EXCLUDED.processing_status,  
                    error_message = EXCLUDED.error_message,  
                    retry_count = EXCLUDED.retry_count,  
                    received_date_time = EXCLUDED.received_date_time;";

            try
            {
                // ✅ Use 'using' instead of 'await using' for IDbConnection  
                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                // ✅ Use CommandDefinition for proper cancellation support  
                var command = new CommandDefinition(
                    sql,
                    notification,
                    cancellationToken: cancellationToken,
                    commandTimeout: 30
                );

                var rowsAffected = await conn.ExecuteAsync(command);

                if (rowsAffected == 0)
                {
                    _logger.LogWarning(
                        "⚠️ No rows affected for NotificationId: {NotificationId}",
                        notification.NotificationId);
                }
                else
                {
                    _logger.LogInformation(
                        "✅ Email notification saved successfully. NotificationId: {NotificationId}, RowsAffected: {RowsAffected}",
                        notification.NotificationId, rowsAffected);
                }

                _telemetry.TrackEvent("EmailNotification_Saved", new Dictionary<string, string>
                {
                    { "NotificationId", notification.NotificationId },
                    { "SubscriptionId", notification.SubscriptionId },
                    { "ChangeType", notification.ChangeType },
                    { "RowsAffected", rowsAffected.ToString() }
                });
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23505") // Unique constraint violation  
            {
                _logger.LogWarning(
                    pgEx,
                    "⚠️ Duplicate notification detected for NotificationId: {NotificationId}",
                    notification.NotificationId);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveNotificationAsync" },
                    { "ErrorType", "DuplicateKey" },
                    { "NotificationId", notification.NotificationId }
                });

                // ✅ Don't throw for duplicates - this is expected in webhook scenarios  
                _logger.LogInformation("Duplicate notification ignored (idempotency): {NotificationId}",
                    notification.NotificationId);
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23503") // Foreign key violation  
            {
                _logger.LogError(
                    pgEx,
                    "❌ Foreign key constraint violation for SubscriptionId: {SubscriptionId}",
                    notification.SubscriptionId);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveNotificationAsync" },
                    { "ErrorType", "ForeignKeyViolation" },
                    { "SubscriptionId", notification.SubscriptionId }
                });

                throw new InvalidOperationException(
                    $"Subscription '{notification.SubscriptionId}' does not exist in the database.",
                    pgEx);
            }
            catch (PostgresException pgEx)
            {
                _logger.LogError(
                    pgEx,
                    "❌ PostgreSQL error while saving notification. SQL State: {SqlState}, Error Code: {ErrorCode}",
                    pgEx.SqlState, pgEx.ErrorCode);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveNotificationAsync" },
                    { "SqlState", pgEx.SqlState ?? "Unknown" },
                    { "NotificationId", notification.NotificationId }
                });

                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "⚠️ Save operation was cancelled for NotificationId: {NotificationId}",
                    notification.NotificationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Unexpected error while saving notification for NotificationId: {NotificationId}",
                    notification.NotificationId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "SaveNotificationAsync" },
                    { "NotificationId", notification.NotificationId }
                });

                throw;
            }
        }
    }
}