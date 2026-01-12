namespace MailNotificationFunctionApp.Models
{
    /// <summary>  
    /// RabbitMQ connection and queue configuration.  
    /// </summary>  
    public class RabbitMqConfiguration
    {
        public string HostName { get; set; } = "localhost";
        public int Port { get; set; } = 5672;
        public string UserName { get; set; } = "guest";
        public string Password { get; set; } = "guest";
        public string VirtualHost { get; set; } = "/";
        public string QueueName { get; set; } = "email-notifications";
        public string ExchangeName { get; set; } = "email-notifications-exchange";
        public string RoutingKey { get; set; } = "notification.email";
        public bool Durable { get; set; } = true;
        public int ConnectionTimeoutSeconds { get; set; } = 30;
        public int PublishTimeoutSeconds { get; set; } = 10;
    }
}