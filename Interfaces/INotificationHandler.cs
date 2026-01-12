using MailNotificationFunctionApp.Models;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
    /// <summary>  
    /// Handles business logic for processing Microsoft Graph change notifications.  
    /// </summary>  
    public interface INotificationHandler
    {
        /// <summary>  
        /// Validates and processes a single Graph notification item.  
        /// </summary>  
        /// <param name="notification">Graph change notification item.</param>  
        /// <param name="rawJson">Raw JSON payload for telemetry reference.</param>  
        /// <param name="cancellationToken">Cancellation token.</param>  
        /// <returns>True if validation and processing succeeded, false otherwise.</returns>  
        /// <exception cref="SecurityException">Thrown when client state validation fails.</exception>  
        Task<bool> HandleAsync(
            GraphNotification.ChangeNotification notification,
            string rawJson,
            CancellationToken cancellationToken = default);
    }
}