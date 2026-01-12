using MailNotificationFunctionApp.Models;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
    /// <summary>  
    /// Publishes messages to a message queue (RabbitMQ).  
    /// </summary>  
    public interface IMessageQueuePublisher
    {
        /// <summary>  
        /// Publishes a notification message to the queue.  
        /// </summary>  
        /// <param name="message">The notification message to publish.</param>  
        /// <param name="cancellationToken">Cancellation token.</param>  
        /// <returns>True if published successfully, false otherwise.</returns>  
        Task<bool> PublishNotificationAsync(
            NotificationQueueMessage message,
            CancellationToken cancellationToken = default);

        /// <summary>  
        /// Checks if the connection to the message queue is healthy.  
        /// </summary>  
        Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
    }
}