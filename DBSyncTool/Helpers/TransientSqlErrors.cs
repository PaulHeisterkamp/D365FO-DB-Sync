using System.IO;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// Classifies errors of a Tier2 (Azure SQL) READ as transient: the connection or session was
    /// lost, or the service was briefly unavailable, so re-running the same read-only query is
    /// safe and likely to succeed. Used by the chunked STREAM route to resume after the last
    /// copied RecId. Deterministic errors (syntax, missing object/column, permissions,
    /// conversion, command timeout) are not transient - they would fail again.
    /// </summary>
    public static class TransientSqlErrors
    {
        /// <summary>SqlException numbers treated as transient.</summary>
        public static readonly IReadOnlySet<int> Numbers = new HashSet<int>
        {
            // Connection / transport (Winsock and SQL network errors)
            64,     // specified network name is no longer available
            121,    // semaphore timeout period has expired
            233,    // no process is on the other end of the pipe / connection closed
            10053,  // connection aborted by the software in the host machine
            10054,  // existing connection was forcibly closed by the remote host
            10060,  // connection attempt / read timed out (network)
            10061,  // connection refused (gateway/failover moment)
            // Session killed or batch aborted by the server
            596,    // cannot continue the execution because the session is in the kill state
            1205,   // deadlock victim
            1219,   // session disconnected because of a high priority DDL operation (online index maintenance)
            3980,   // request failed because the batch was aborted (abort signal / other request)
            6005,   // SHUTDOWN is in progress
            // Azure SQL Database transient errors
            4060,   // cannot open database (during a failover)
            4221,   // login to read-secondary failed (HADR transition) - ApplicationIntent=ReadOnly
            40143,  // service has encountered an error processing your request
            40197,  // service has encountered an error processing your request (reconfiguration)
            40501,  // service is currently busy
            40540,  // service has encountered an error processing your request
            40613,  // database is not currently available
            49918,  // not enough resources to process request
            49919,  // too many create/update operations in progress
            49920,  // too many operations in progress
            10928,  // resource limit reached (workers/sessions)
            10929,  // resource limit / minimum guarantee
        };

        private static readonly string[] MessagePatterns =
        {
            "transport-level error",
            "forcibly closed",
            "severe error occurred on the current command",
            "connection was closed",
            "connection is broken",
            "physical connection is not usable",
            "session has been disconnected",
            "semaphore timeout",
        };

        /// <summary>True when <paramref name="ex"/> (or an inner exception) is a transient connection/session error.</summary>
        public static bool IsTransient(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is SqlException sql)
                {
                    if (Numbers.Contains(sql.Number))
                        return true;
                    foreach (SqlError error in sql.Errors)
                    {
                        if (Numbers.Contains(error.Number))
                            return true;
                    }
                }

                if (e is SocketException || e is IOException)
                    return true;

                if ((e is SqlException || e is InvalidOperationException) &&
                    MessagePatterns.Any(p => e.Message.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        /// <summary>Short description for the log: "SqlException 10054: message".</summary>
        public static string Describe(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null && e is not SqlException)
                e = e.InnerException;
            return e is SqlException sql ? $"SqlException {sql.Number}: {sql.Message}" : $"{e.GetType().Name}: {e.Message}";
        }
    }
}
