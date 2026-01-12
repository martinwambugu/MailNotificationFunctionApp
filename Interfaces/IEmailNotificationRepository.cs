using MailNotificationFunctionApp.Models;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
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
    /// </remarks>  
    public interface IEmailNotificationRepository
    {
        /// <summary>  
        /// Persists an email notification record into the PostgreSQL database.  
        /// </summary>  
        /// <param name="notification">Notification entity to save.</param>  
        /// <param name="cancellationToken">  
        /// A cancellation token to cancel the save operation.  
        /// </param>  
        /// <returns>  
        /// A <see cref="Task"/> representing the asynchronous save operation.  
        /// </returns>  
        /// <exception cref="ArgumentNullException">  
        /// Thrown when <paramref name="notification"/> is null.  
        /// </exception>  
        /// <exception cref="ArgumentException">  
        /// Thrown when required fields (NotificationId, SubscriptionId) are empty or invalid.  
        /// </exception>  
        /// <exception cref="InvalidOperationException">  
        /// Thrown when the subscription referenced by SubscriptionId does not exist (foreign key violation).  
        /// </exception>  
        /// <exception cref="OperationCanceledException">  
        /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.  
        /// </exception>  
        /// <remarks>  
        /// <para>  
        /// This method uses UPSERT logic (INSERT ... ON CONFLICT DO UPDATE) to handle duplicate notifications.  
        /// Duplicate notifications are logged but do not throw exceptions, supporting idempotency.  
        /// </para>  
        /// <para>  
        /// <b>Performance:</b> Uses parameterized queries and connection pooling for optimal performance.  
        /// </para>  
        /// </remarks>  
        Task SaveNotificationAsync(
            EmailNotification notification,
            CancellationToken cancellationToken = default);
    }
}