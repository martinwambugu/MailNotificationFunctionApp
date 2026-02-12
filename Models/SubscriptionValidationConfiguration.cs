using System;

namespace MailNotificationFunctionApp.Models
{
    /// <summary>
    /// Configuration options for subscription validation behavior in MailNotificationFunctionApp.
    /// </summary>
    public class SubscriptionValidationConfiguration
    {
        /// <summary>
        /// Grace period (in minutes) applied after subscription expiration during which
        /// notifications are still accepted. This accounts for processing delays and clock skew.
        /// </summary>
        public int ExpirationGracePeriodMinutes { get; set; } = 5;

        /// <summary>
        /// When true, subscriptions that have technically expired but are within the grace
        /// period are treated as valid for client state validation.
        /// </summary>
        public bool AllowExpiredWithinGracePeriod { get; set; } = true;

        /// <summary>
        /// When true, additional diagnostic logging is emitted during validation.
        /// (Reserved for future use.)
        /// </summary>
        public bool LogDetailedDiagnostics { get; set; } = true;
    }
}

