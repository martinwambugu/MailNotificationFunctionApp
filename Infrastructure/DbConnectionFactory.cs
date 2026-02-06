using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using MailNotificationFunctionApp.Interfaces;

namespace MailNotificationFunctionApp.Infrastructure
{
    /// <summary>  
    /// PostgreSQL implementation of <see cref="IDbConnectionFactory"/> with connection pooling.  
    /// </summary>  
    public class DbConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;
        private readonly ILogger<DbConnectionFactory> _logger;

        public DbConnectionFactory(IConfiguration configuration, ILogger<DbConnectionFactory> logger)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(logger);

            _logger = logger;

            var connectionString = configuration.GetConnectionString("PostgreSqlConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("❌ Database connection string 'PostgreSqlConnection' is not configured.");
                throw new InvalidOperationException(
                    "Database connection string is not configured. " +
                    "Please set it in appsettings.json, local.settings.json, or Azure Key Vault.");
            }

            // ✅ Configure connection pooling
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = true,
                MinPoolSize = 5,
                MaxPoolSize = 100,
                ConnectionIdleLifetime = 300,
                ConnectionPruningInterval = 10,
                CommandTimeout = 30,
                Timeout = 15,
                NoResetOnClose = false,  // ✅ SAFE: Always reset connection state on return to pool (prevents transaction state leakage)
                MaxAutoPrepare = 20,
                AutoPrepareMinUsages = 2,
                SslMode = SslMode.Require
                // TrustServerCertificate removed - obsolete in Npgsql 10.0+
            };

            _connectionString = builder.ToString();

            var safeConnString = $"Host={builder.Host};Port={builder.Port};Database={builder.Database};" +
                                $"Username={builder.Username};Password=****;Pooling={builder.Pooling};" +
                                $"MinPoolSize={builder.MinPoolSize};MaxPoolSize={builder.MaxPoolSize}";
            _logger.LogInformation("📡 PostgreSQL connection factory initialized: {ConnectionString}", safeConnString);
        }

        /// <inheritdoc/>  
        public async Task<NpgsqlConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Creating new PostgreSQL connection from pool...");

            NpgsqlConnection? connection = null;
            try
            {
                connection = new NpgsqlConnection(_connectionString);

                connection.StateChange += (sender, args) =>
                {
                    _logger.LogDebug(
                        "🔌 Connection state changed: {OriginalState} → {CurrentState}",
                        args.OriginalState, args.CurrentState);
                };

                await connection.OpenAsync(cancellationToken);

                _logger.LogDebug("✅ PostgreSQL connection opened. State: {State}", connection.State);

                return connection;
            }
            catch (NpgsqlException npgEx)
            {
                _logger.LogError(
                    npgEx,
                    "❌ Failed to open PostgreSQL connection. Error Code: {ErrorCode}, SQL State: {SqlState}",
                    npgEx.ErrorCode, npgEx.SqlState);

                connection?.Dispose();

                throw new InvalidOperationException(
                    $"Failed to connect to PostgreSQL database. Error: {npgEx.Message}",
                    npgEx);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ Connection opening was cancelled.");
                connection?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unexpected error while opening database connection.");
                connection?.Dispose();
                throw;
            }
        }
    }
}