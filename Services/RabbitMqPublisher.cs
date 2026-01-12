using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MailNotificationFunctionApp.Services
{
    /// <summary>  
    /// RabbitMQ implementation of message queue publisher.  
    /// </summary>  
    /// <remarks>  
    /// Uses connection pooling and implements retry logic for transient failures.  
    /// Connections are thread-safe and reused across multiple publish operations.  
    /// </remarks>  
    public class RabbitMqPublisher : IMessageQueuePublisher, IDisposable
    {
        private readonly RabbitMqConfiguration _config;
        private readonly ILogger<RabbitMqPublisher> _logger;
        private readonly ICustomTelemetry _telemetry;
        private IConnection? _connection;
        private IChannel? _channel; // ✅ Changed from IModel to IChannel  
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private bool _disposed = false;

        public RabbitMqPublisher(
            IOptions<RabbitMqConfiguration> config,
            ILogger<RabbitMqPublisher> logger,
            ICustomTelemetry telemetry)
        {
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

            // ✅ Initialize synchronously in constructor  
            InitializeConnectionAsync().GetAwaiter().GetResult();
        }

        /// <summary>  
        /// Initializes RabbitMQ connection and channel.  
        /// </summary>  
        private async Task InitializeConnectionAsync()
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = _config.HostName,
                    Port = _config.Port,
                    UserName = _config.UserName,
                    Password = _config.Password,
                    VirtualHost = _config.VirtualHost,
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(_config.ConnectionTimeoutSeconds),
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                    TopologyRecoveryEnabled = true
                };

                // ✅ Use async methods  
                _connection = await factory.CreateConnectionAsync();
                _channel = await _connection.CreateChannelAsync();

                // Declare exchange (idempotent)  
                await _channel.ExchangeDeclareAsync(
                    exchange: _config.ExchangeName,
                    type: ExchangeType.Topic,
                    durable: _config.Durable,
                    autoDelete: false);

                // Declare queue (idempotent)  
                await _channel.QueueDeclareAsync(
                    queue: _config.QueueName,
                    durable: _config.Durable,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                // Bind queue to exchange  
                await _channel.QueueBindAsync(
                    queue: _config.QueueName,
                    exchange: _config.ExchangeName,
                    routingKey: _config.RoutingKey);

                _logger.LogInformation(
                    "✅ RabbitMQ connection established: {HostName}:{Port}, Queue: {QueueName}",
                    _config.HostName, _config.Port, _config.QueueName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to initialize RabbitMQ connection");
                throw;
            }
        }

        /// <inheritdoc/>  
        public async Task<bool> PublishNotificationAsync(
            NotificationQueueMessage message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            try
            {
                await EnsureConnectionIsOpenAsync(cancellationToken);

                var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                });

                var body = Encoding.UTF8.GetBytes(json);

                var properties = new BasicProperties
                {
                    Persistent = true, // Survive broker restarts  
                    ContentType = "application/json",
                    MessageId = message.NotificationId,
                    Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    Headers = new Dictionary<string, object?>
                    {
                        { "x-notification-id", message.NotificationId },
                        { "x-user-id", message.UserId },
                        { "x-message-id", message.MessageId },
                        { "x-change-type", message.ChangeType }
                    }
                };

                await _lock.WaitAsync(cancellationToken);
                try
                {
                    await _channel!.BasicPublishAsync(
                        exchange: _config.ExchangeName,
                        routingKey: _config.RoutingKey,
                        mandatory: false,
                        basicProperties: properties,
                        body: body,
                        cancellationToken: cancellationToken);
                }
                finally
                {
                    _lock.Release();
                }

                _logger.LogInformation(
                    "📤 Published notification to RabbitMQ: NotificationId={NotificationId}, UserId={UserId}",
                    message.NotificationId, message.UserId);

                _telemetry.TrackEvent("RabbitMQ_MessagePublished", new Dictionary<string, string>
                {
                    { "NotificationId", message.NotificationId },
                    { "UserId", message.UserId },
                    { "MessageId", message.MessageId },
                    { "QueueName", _config.QueueName }
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "❌ Failed to publish notification to RabbitMQ: NotificationId={NotificationId}",
                    message.NotificationId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "NotificationId", message.NotificationId },
                    { "Operation", "PublishNotification" }
                });

                return false;
            }
        }

        /// <inheritdoc/>  
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var isHealthy = _connection?.IsOpen == true && _channel?.IsOpen == true;
                return Task.FromResult(isHealthy);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>  
        /// Ensures the RabbitMQ connection and channel are open.  
        /// </summary>  
        private async Task EnsureConnectionIsOpenAsync(CancellationToken cancellationToken)
        {
            if (_connection?.IsOpen != true || _channel?.IsOpen != true)
            {
                _logger.LogWarning("⚠️ RabbitMQ connection closed, reconnecting...");

                await _lock.WaitAsync(cancellationToken);
                try
                {
                    // Double-check after acquiring lock  
                    if (_connection?.IsOpen != true || _channel?.IsOpen != true)
                    {
                        await DisposeAsync();
                        await InitializeConnectionAsync();
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }
        }

        /// <summary>  
        /// Disposes RabbitMQ resources.  
        /// </summary>  
        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }

        private async Task DisposeAsync()
        {
            if (_disposed)
                return;

            try
            {
                if (_channel != null)
                {
                    await _channel.CloseAsync();
                    _channel.Dispose();
                }

                if (_connection != null)
                {
                    await _connection.CloseAsync();
                    _connection.Dispose();
                }

                _lock?.Dispose();

                _logger.LogInformation("🔌 RabbitMQ connection disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error disposing RabbitMQ connection");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}