using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
    /// <summary>  
    /// Provides a factory abstraction for creating database connections.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// Implementations should return an already-opened connection ready for immediate use.  
    /// Callers are responsible for disposing the connection using the <c>await using</c> pattern.  
    /// </para>  
    /// <para>  
    /// <b>Thread Safety:</b> This interface is thread-safe and can be called concurrently.  
    /// </para>  
    /// </remarks>  
    public interface IDbConnectionFactory
    {
        /// <summary>  
        /// Creates and opens a new database connection asynchronously.  
        /// </summary>  
        /// <param name="cancellationToken">  
        /// A cancellation token to cancel the connection opening operation.  
        /// </param>  
        /// <returns>  
        /// A <see cref="Task{IDbConnection}"/> representing the asynchronous operation.  
        /// The task result contains an opened database connection.  
        /// </returns>  
        /// <exception cref="InvalidOperationException">  
        /// Thrown when the connection string is not configured or connection fails.  
        /// </exception>  
        /// <exception cref="OperationCanceledException">  
        /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.  
        /// </exception>  
        Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
    }
}