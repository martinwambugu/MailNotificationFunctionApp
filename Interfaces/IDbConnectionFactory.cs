using System.Data;

namespace MailNotificationFunctionApp.Interfaces
{
    /// <summary>  
    /// Defines a factory contract for creating database connections.  
    /// This abstraction allows the application to request a new database connection  
    /// without being tightly coupled to a specific database provider (e.g., PostgreSQL, SQL Server).  
    /// </summary>  
    /// <remarks>  
    /// Implementations of this interface should:  
    /// <list type="bullet">  
    ///     <item>Retrieve the connection string from a secure configuration source (e.g., Azure Key Vault, app settings).</item>  
    ///     <item>Instantiate and return an <see cref="IDbConnection"/> object specific to the target database provider.</item>  
    ///     <item>NOT open the connection — responsibility for opening/closing the connection lies with the calling code.</item>  
    /// </list>  
    /// This interface supports Dependency Injection (DI) to decouple database connection logic  
    /// from repository/service classes, making the application easier to test and maintain.  
    /// </remarks>  
    public interface IDbConnectionFactory
    {
        /// <summary>  
        /// Creates a new instance of an <see cref="IDbConnection"/>.  
        /// </summary>  
        /// <returns>  
        /// An un-opened <see cref="IDbConnection"/> instance configured for the target database.  
        /// </returns>  
        /// <example>  
        /// Example usage:  
        /// <code>  
        /// using var connection = _dbConnectionFactory.CreateConnection();  
        /// connection.Open();  
        /// var result = connection.Query&lt;Borrower&gt;("SELECT * FROM Borrower;");  
        /// </code>  
        /// </example>  
        IDbConnection CreateConnection();
    }
}