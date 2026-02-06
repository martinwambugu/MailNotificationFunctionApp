using Npgsql;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
    /// <summary>  
    /// Provides a factory abstraction for creating PostgreSQL database connections.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// Returns <see cref="NpgsqlConnection"/> instead of <see cref="IDbConnection"/>  
    /// to support PostgreSQL-specific features like async transactions.  
    /// </para>  
    /// <para>  
    /// <b>Thread Safety:</b> This interface is thread-safe and can be called concurrently.  
    /// </para>  
    /// </remarks>  
    public interface IDbConnectionFactory
    {
        /// <summary>  
        /// Creates and opens a new PostgreSQL database connection asynchronously.  
        /// </summary>  
        /// <param name="cancellationToken">  
        /// A cancellation token to cancel the connection opening operation.  
        /// </param>  
        /// <returns>  
        /// A <see cref="Task{NpgsqlConnection}"/> representing the asynchronous operation.  
        /// The task result contains an opened PostgreSQL connection.  
        /// </returns>  
        /// <exception cref="InvalidOperationException">  
        /// Thrown when the connection string is not configured or connection fails.  
        /// </exception>  
        /// <exception cref="OperationCanceledException">  
        /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.  
        /// </exception>  
        Task<NpgsqlConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
    }
}