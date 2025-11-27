using MailNotificationFunctionApp.Models;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Interfaces
{
    /// <summary>  
    /// Defines persistence operations for <see cref="EmailNotification"/> entities.  
    /// </summary>  
    public interface IEmailNotificationRepository
    {
        /// <summary>  
        /// Persists an email notification record into the PostgreSQL database.  
        /// </summary>  
        /// <param name="notification">Notification entity to save.</param>  
        Task SaveNotificationAsync(EmailNotification notification);
    }
}