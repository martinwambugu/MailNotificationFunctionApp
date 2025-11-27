namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// Represents the validation response sent back to Microsoft Graph.  
    /// </summary>  
    public class ValidationResponse
    {
        public string ValidationToken { get; set; } = default!;
    }
}