using MailNotificationFunctionApp.Models;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
    /// <summary>  
    /// Valid processing statuses for email notifications.  
    /// Must match database CHECK constraint on emailnotifications.processingstatus.  
    /// </summary>  
    public enum NotificationProcessingStatus
    {
        /// <summary>Notification received but not yet processed.</summary>  
        Pending,

        /// <summary>Notification currently being processed.</summary>  
        Processing,

        /// <summary>Notification processing completed successfully.</summary>  
        Completed,

        /// <summary>Notification processing failed after retries.</summary>  
        Failed
    }

    /// <summary>  
    /// Result of saving an email notification.  
    /// </summary>  
    public enum SaveNotificationResult
    {
        /// <summary>Notification was newly inserted.</summary>  
        Inserted,

        /// <summary>Notification was updated (ON CONFLICT).</summary>  
        Updated,

        /// <summary>Notification was a duplicate and ignored.</summary>  
        Duplicate
    }

    /// <summary>  
    /// Defines persistence operations for <see cref="EmailNotification"/> entities.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This interface abstracts database operations for email notifications,  
    /// allowing for easy testing and potential database provider changes.  
    /// </para>  
    /// <para>  
    /// <b>Thread Safety:</b> Implementations should be thread-safe and support concurrent calls.  
    /// </para>  
    /// <para>  
    /// <b>Key Features:</b>  
    /// - Idempotent saves using ON CONFLICT DO UPDATE  
    /// - Row-level locking for distributed processing (FOR UPDATE SKIP LOCKED)  
    /// - Transaction-aware updates for atomic operations  
    /// - Enum-based status validation preventing invalid database states  
    /// </para>  
    /// </remarks>  
    public interface IEmailNotificationRepository
    {
        /// <summary>  
        /// Persists an email notification record into the PostgreSQL database with idempotency support.  
        /// </summary>  
        /// <param name="notification">Notification entity to save.</param>  
        /// <param name="cancellationToken">  
        /// A cancellation token to cancel the save operation.  
        /// </param>  
        /// <returns>  
        /// A <see cref="Task{SaveNotificationResult}"/> indicating whether the notification was inserted, updated, or duplicate.  
        /// </returns>  
        /// <exception cref="ArgumentNullException">  
        /// Thrown when <paramref name="notification"/> is null.  
        /// </exception>  
        /// <exception cref="ArgumentException">  
        /// Thrown when required fields (NotificationId, SubscriptionId) are empty or invalid,  
        /// or when ProcessingStatus contains an invalid value.  
        /// </exception>  
        /// <exception cref="InvalidOperationException">  
        /// Thrown when a database constraint is violated (e.g., check constraint on status).  
        /// </exception>  
        /// <exception cref="OperationCanceledException">  
        /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.  
        /// </exception>  
        /// <remarks>  
        /// <para>  
        /// This method uses UPSERT logic (INSERT ... ON CONFLICT DO UPDATE) to handle duplicate notifications.  
        /// The return value indicates whether the notification was newly inserted, updated, or was a duplicate.  
        /// </para>  
        /// <para>  
        /// <b>Performance:</b> Uses parameterized queries and connection pooling for optimal performance.  
        /// </para>  
        /// <para>  
        /// <b>Idempotency:</b> Safe to call multiple times with the same notification ID.  
        /// </para>  
        /// </remarks>  
        Task<SaveNotificationResult> SaveNotificationAsync(
            EmailNotification notification,
            CancellationToken cancellationToken = default);

        /// <summary>  
        /// Retrieves and locks pending or failed notifications that are eligible for retry.  
        /// Uses PostgreSQL row-level locking to prevent duplicate processing by multiple workers.  
        /// </summary>  
        /// <param name="batchSize">  
        /// Maximum number of notifications to retrieve and lock. Must be between 1 and 100.  
        /// Default is 10.  
        /// </param>  
        /// <param name="maxRetryCount">  
        /// Maximum number of retry attempts allowed. Notifications exceeding this count are excluded.  
        /// Default is 3.  
        /// </param>  
        /// <param name="cancellationToken">  
        /// A cancellation token to cancel the retrieval operation.  
        /// </param>  
        /// <returns>  
        /// A collection of locked <see cref="EmailNotification"/> entities with status 'Pending' or 'Failed'  
        /// that have not exceeded the retry limit. Returns an empty collection if none found.  
        /// </returns>  
        /// <exception cref="ArgumentOutOfRangeException">  
        /// Thrown when <paramref name="batchSize"/> is not between 1 and 100,  
        /// or when <paramref name="maxRetryCount"/> is negative.  
        /// </exception>  
        /// <exception cref="OperationCanceledException">  
        /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.  
        /// </exception>  
        /// <remarks>  
        /// <para>  
        /// Uses <c>FOR UPDATE SKIP LOCKED</c> to ensure notifications are processed by only one worker.  
        /// Locked rows remain locked until the transaction is committed or rolled back.  
        /// </para>  
        /// <para>  
        /// Results are ordered by <c>receiveddatetime</c> (oldest first) to ensure FIFO processing.  
        /// </para>  
        /// <para>  
        /// <b>Use Case:</b> This method is typically called by background workers or timer functions  
        /// to fetch and process notifications in a distributed system without race conditions.  
        /// </para>  
        /// <para>  
        /// <b>Important:</b> Caller should use a transaction to ensure locks are properly released.  
        /// </para>  
        /// </remarks>  
        Task<IEnumerable<EmailNotification>> GetAndLockPendingNotificationsAsync(
            int batchSize = 10,
            int maxRetryCount = 3,
            CancellationToken cancellationToken = default);

        /// <summary>  
        /// Updates the processing status of an existing notification.  
        /// </summary>  
        /// <param name="notificationId">  
        /// The unique identifier of the notification to update.  
        /// </param>  
        /// <param name="status">  
        /// The new processing status (enum-validated to match database constraints).  
        /// </param>  
        /// <param name="errorMessage">  
        /// Optional error message to record if the status is 'Failed'. Set to null for successful operations.  
        /// </param>  
        /// <param name="cancellationToken">  
        /// A cancellation token to cancel the update operation.  
        /// </param>  
        /// <returns>  
        /// A <see cref="Task"/> representing the asynchronous update operation.  
        /// </returns>  
        /// <exception cref="ArgumentException">  
        /// Thrown when <paramref name="notificationId"/> is null or whitespace.  
        /// </exception>  
        /// <exception cref="InvalidOperationException">  
        /// Thrown when the notification is not found in the database.  
        /// </exception>  
        /// <exception cref="OperationCanceledException">  
        /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.  
        /// </exception>  
        /// <remarks>  
        /// <para>  
        /// This method automatically increments the <c>retrycount</c> field ONLY when status is 'Failed',  
        /// and updates the <c>receiveddatetime</c> to the current timestamp.  
        /// </para>  
        /// <para>  
        /// <b>Status Validation:</b> The <paramref name="status"/> parameter is an enum, ensuring  
        /// only valid values (Pending, Processing, Completed, Failed) can be set, preventing  
        /// database constraint violations.  
        /// </para>  
        /// <para>  
        /// <b>Error Handling:</b> Throws <see cref="InvalidOperationException"/> if notification not found,  
        /// unlike the old signature which logged a warning and continued silently.  
        /// </para>  
        /// </remarks>  
        Task UpdateNotificationStatusAsync(
            string notificationId,
            NotificationProcessingStatus status,
            string? errorMessage = null,
            CancellationToken cancellationToken = default);

        /// <summary>  
        /// Updates notification status within an existing database transaction.  
        /// Used for atomic operations that span multiple database calls.  
        /// </summary>  
        /// <param name="notificationId">The notification ID to update.</param>  
        /// <param name="status">New processing status (enum-validated).</param>  
        /// <param name="transaction">Active database transaction.</param>  
        /// <param name="errorMessage">Optional error message for failed notifications.</param>  
        /// <param name="cancellationToken">Cancellation token.</param>  
        /// <returns>A <see cref="Task"/> representing the asynchronous update operation.</returns>  
        /// <exception cref="ArgumentException">  
        /// Thrown when <paramref name="notificationId"/> is null or whitespace.  
        /// </exception>  
        /// <exception cref="ArgumentNullException">  
        /// Thrown when <paramref name="transaction"/> is null.  
        /// </exception>  
        /// <exception cref="InvalidOperationException">  
        /// Thrown when the notification is not found in the database.  
        /// </exception>  
        /// <remarks>  
        /// <para>  
        /// <b>Use Case:</b> When updating notification status must be atomic with other database operations  
        /// (e.g., saving journaled email + updating notification status).  
        /// </para>  
        /// <para>  
        /// <b>Example:</b>  
        /// <code>  
        /// using var transaction = await connection.BeginTransactionAsync();  
        /// try  
        /// {  
        ///     await _emailRepo.SaveJournaledEmailAsync(email, transaction, ct);  
        ///     await _notificationRepo.UpdateNotificationStatusInTransactionAsync(  
        ///         notificationId, NotificationProcessingStatus.Completed, transaction, ct);  
        ///     await transaction.CommitAsync(ct);  
        /// }  
        /// catch  
        /// {  
        ///     await transaction.RollbackAsync(ct);  
        ///     throw;  
        /// }  
        /// </code>  
        /// </para>  
        /// </remarks>  
        Task UpdateNotificationStatusInTransactionAsync(
            string notificationId,
            NotificationProcessingStatus status,
            IDbTransaction transaction,
            string? errorMessage = null,
            CancellationToken cancellationToken = default);

        /// <summary>  
        /// Retrieves a specific notification by its unique identifier.  
        /// </summary>  
        /// <param name="notificationId">  
        /// The unique identifier of the notification to retrieve.  
        /// </param>  
        /// <param name="cancellationToken">  
        /// A cancellation token to cancel the retrieval operation.  
        /// </param>  
        /// <returns>  
        /// The <see cref="EmailNotification"/> entity if found; otherwise, null.  
        /// </returns>  
        /// <exception cref="ArgumentException">  
        /// Thrown when <paramref name="notificationId"/> is null or whitespace.  
        /// </exception>  
        /// <exception cref="OperationCanceledException">  
        /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.  
        /// </exception>  
        /// <remarks>  
        /// <para>  
        /// <b>Use Case:</b> Useful for checking if a notification has already been processed  
        /// before attempting to process it again (idempotency check).  
        /// </para>  
        /// </remarks>  
        Task<EmailNotification?> GetNotificationByIdAsync(
            string notificationId,
            CancellationToken cancellationToken = default);

        /// <summary>  
        /// Retrieves all notifications for a specific subscription.  
        /// </summary>  
        /// <param name="subscriptionId">  
        /// The unique identifier of the subscription.  
        /// </param>  
        /// <param name="limit">  
        /// Maximum number of records to return. Must be between 1 and 1000. Default is 100.  
        /// </param>  
        /// <param name="cancellationToken">  
        /// A cancellation token to cancel the retrieval operation.  
        /// </param>  
        /// <returns>  
        /// A collection of <see cref="EmailNotification"/> entities associated with the subscription.  
        /// Returns an empty collection if none found.  
        /// </returns>  
        /// <exception cref="ArgumentException">  
        /// Thrown when <paramref name="subscriptionId"/> is null or whitespace.  
        /// </exception>  
        /// <exception cref="ArgumentOutOfRangeException">  
        /// Thrown when <paramref name="limit"/> is not between 1 and 1000.  
        /// </exception>  
        /// <exception cref="OperationCanceledException">  
        /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.  
        /// </exception>  
        /// <remarks>  
        /// <para>  
        /// Results are ordered by <c>notificationdatetime</c> (newest first).  
        /// </para>  
        /// <para>  
        /// <b>Use Case:</b> Useful for auditing or debugging subscription-specific notification flows.  
        /// </para>  
        /// </remarks>  
        Task<IEnumerable<EmailNotification>> GetNotificationsBySubscriptionAsync(
            string subscriptionId,
            int limit = 100,
            CancellationToken cancellationToken = default);
    }
}