using System;
using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using MailNotificationFunctionApp.Interfaces;

namespace MailNotificationFunctionApp.Infrastructure
{
    /// <summary>  
    /// PostgreSQL implementation of <see cref="IDbConnectionFactory"/>.  
    /// </summary>  
    /// <remarks>  
    /// Retrieves the connection string from configuration and creates an un-opened <see cref="IDbConnection"/>.  
    /// Logs connection creation events for diagnostics.  
    /// </remarks>  
    public class DbConnectionFactory : IDbConnectionFactory
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DbConnectionFactory> _logger;

        /// <summary>  
        /// Initializes a new instance of the <see cref="DbConnectionFactory"/> class.  
        /// </summary>  
        /// <param name="configuration">Application configuration (used to retrieve the connection string).</param>  
        /// <param name="logger">Logger for telemetry and diagnostics.</param>  
        public DbConnectionFactory(IConfiguration configuration, ILogger<DbConnectionFactory> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>  
        public IDbConnection CreateConnection()
        {
            var connectionString = _configuration.GetConnectionString("PostgreSqlConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("❌ Database connection string is not configured.");
                throw new InvalidOperationException("Database connection string is not configured.");
            }

            // Mask password before logging  
            var safeConn = connectionString.Replace(
                new NpgsqlConnectionStringBuilder(connectionString).Password, "****");

            _logger.LogInformation("📡 Creating new PostgreSQL connection to: {Conn}", safeConn);
            var conn = new NpgsqlConnection(connectionString);

            // Optional: log when connection is opened  
            conn.StateChange += (sender, args) =>
            {
                _logger.LogInformation("🔌 DB Connection state changed: {Old} -> {New}", args.OriginalState, args.CurrentState);
            };

            return conn;
        }
    }
}