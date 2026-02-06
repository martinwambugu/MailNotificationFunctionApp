using MailNotificationFunctionApp.Models;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
    /// <summary>  
    /// Defines the contract for processing email notifications from Microsoft Graph.  
    /// </summary>  
    public interface INotificationHandler
    {
        /// <summary>  
        /// Processes a single notification item.  
        /// </summary>  
        /// <param name="item">The notification item to process.</param>  
        /// <param name="rawPayload">The original raw JSON payload from Microsoft Graph.</param>  
        /// <param name="cancellationToken">Cancellation token.</param>  
        /// <returns>  
        /// <c>true</c> if processing succeeded; <c>false</c> if processing failed but should be retried.  
        /// </returns>  
        /// <exception cref="System.Security.SecurityException">  
        /// Thrown when client state validation fails (spoofing attempt detected).  
        /// </exception>  
        Task<bool> HandleAsync(
            NotificationItem item,
            string rawPayload,
            CancellationToken cancellationToken = default);
    }
}