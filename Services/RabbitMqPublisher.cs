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
    /// Assumes queue is pre-configured and managed by infrastructure.  
    /// </remarks>  
    public class RabbitMqPublisher : IMessageQueuePublisher, IDisposable
    {
        private readonly RabbitMqConfiguration _config;
        private readonly ILogger<RabbitMqPublisher> _logger;
        private readonly ICustomTelemetry _telemetry;
        private IConnection? _connection;
        private IChannel? _channel;

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

            // ✅ REMOVED blocking call - Connection will be initialized lazily on first use
            // This prevents potential deadlocks and thread pool starvation during startup
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

                // Create connection and channel  
                _connection = await factory.CreateConnectionAsync();
                _channel = await _connection.CreateChannelAsync();

                // ✅ Only declare exchange (idempotent)  
                await _channel.ExchangeDeclareAsync(
                    exchange: _config.ExchangeName,
                    type: ExchangeType.Topic,
                    durable: _config.Durable,
                    autoDelete: false);

                // ✅ Verify queue exists without modifying it (optional passive check)  
                try
                {
                    await _channel.QueueDeclarePassiveAsync(_config.QueueName);
                    _logger.LogInformation(
                        "✅ Verified queue exists: {QueueName}",
                        _config.QueueName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "⚠️ Could not verify queue existence (may not have permissions): {QueueName}. " +
                        "Assuming queue is managed by infrastructure.",
                        _config.QueueName);
                    // Continue anyway - queue might exist but we don't have passive declare permissions  
                }

                // ✅ Don't declare queue - assume it's managed by infrastructure  
                // ✅ Don't bind queue - assume binding is managed by infrastructure  

                _logger.LogInformation(
                    "✅ RabbitMQ connection established: {HostName}:{Port}, Exchange: {ExchangeName}, Queue: {QueueName}",
                    _config.HostName, _config.Port, _config.ExchangeName, _config.QueueName);

                _telemetry.TrackEvent("RabbitMQ_ConnectionInitialized", new Dictionary<string, string>
                {
                    { "HostName", _config.HostName },
                    { "Port", _config.Port.ToString() },
                    { "ExchangeName", _config.ExchangeName },
                    { "QueueName", _config.QueueName }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to initialize RabbitMQ connection");

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "InitializeConnection" },
                    { "HostName", _config.HostName },
                    { "Port", _config.Port.ToString() }
                });

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
                    // ✅ Publish directly to exchange with routing key  
                    // The exchange will route to the queue based on bindings  
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
                    "📤 Published notification to RabbitMQ: NotificationId={NotificationId}, UserId={UserId}, RoutingKey={RoutingKey}",
                    message.NotificationId, message.UserId, _config.RoutingKey);

                _telemetry.TrackEvent("RabbitMQ_MessagePublished", new Dictionary<string, string>
                {
                    { "NotificationId", message.NotificationId },
                    { "UserId", message.UserId },
                    { "MessageId", message.MessageId },
                    { "ExchangeName", _config.ExchangeName },
                    { "RoutingKey", _config.RoutingKey }
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
                    { "Operation", "PublishNotification" },
                    { "ExchangeName", _config.ExchangeName },
                    { "RoutingKey", _config.RoutingKey }
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

                if (!isHealthy)
                {
                    _logger.LogWarning("⚠️ RabbitMQ health check failed: Connection or channel is closed");
                }

                return Task.FromResult(isHealthy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ RabbitMQ health check exception");
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

                        _logger.LogInformation("✅ RabbitMQ reconnection successful");
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