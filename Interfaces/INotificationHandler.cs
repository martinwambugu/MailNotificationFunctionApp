using MailNotificationFunctionApp.Models;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
    /// <summary>  
    /// Handles business logic for processing Microsoft Graph change notifications.  
    /// </summary>  
    public interface INotificationHandler
    {
        /// <summary>  
        /// Processes a single Graph notification item.  
        /// </summary>  
        /// <param name="notification">Graph change notification item.</param>  
        /// <param name="rawJson">Raw JSON payload for telemetry reference.</param>  
        Task HandleAsync(GraphNotification.ChangeNotification notification, string rawJson);
    }
}