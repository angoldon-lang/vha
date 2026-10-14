using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Sql;
using static Vha.Infrastructure.Sql.SqlHelper;

namespace Vha.Infrastructure.Sql
{
    /// <summary>Connects to the SQL instance of another node (the one that has to release the databases).</summary>
    public sealed class SqlNodeConnector : INodeConnector
    {
        private readonly ILog _log;
        private readonly int _releaseTimeoutSeconds;
        public SqlNodeConnector(ILog log, int releaseTimeoutSeconds = 300) { _log = log; _releaseTimeoutSeconds = releaseTimeoutSeconds; }

        // Errors that mean "I reached the instance but I am not allowed in / the configuration is wrong".
        // They must abort the failover: they say nothing about the node being down.
        private static readonly HashSet<int> ConfigurationErrors = new HashSet<int>
        { 18452, 18456, 18461, 18462, 18463, 18464, 18465, 18466, 18467, 18468, 18470, 18471, 18486, 18487, 18488, 4060, 4064, 916, 40615 };

        /// <summary>
        /// Decides what a failed Open() means. Returns null for configuration/authentication problems (the caller rethrows).
        /// Everything else raised while connecting (refused, unresolved, unreachable, timed out) is "the node does not answer":
        /// the number alone is not reliable (.NET Framework reports -1/53/258, .NET Core reports 0).
        /// A TLS/certificate failure is also a configuration problem, not a dead node.
        /// </summary>
        internal static ConnectStatus? Classify(SqlException ex)
        {
            foreach (SqlError e in ex.Errors)
                if (ConfigurationErrors.Contains(e.Number)) return null;
            var m = ex.Message ?? "";
            // A timeout comes first: a frozen instance reports "Connection Timeout Expired ... while attempting to consume the pre-login handshake",
            // which must not be mistaken for a TLS handshake failure.
            if (ex.Number == -2 || ex.Number == 258 || IsTimeoutText(m)) return ConnectStatus.Timeout;
            // A TLS/certificate problem is a configuration error; a connection reset during the handshake is only an instance that is still starting
            // (or going down): look at the whole exception chain, not just the outer text.
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                var t = e.Message ?? "";
                if (t.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("SSL Provider", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("cipher", StringComparison.OrdinalIgnoreCase) >= 0 || e.GetType().Name.IndexOf("Authentication", StringComparison.OrdinalIgnoreCase) >= 0)
                    return null;
            }
            return ConnectStatus.NotAvailable;
        }

        private static bool IsTimeoutText(string m) =>
            m.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("timeout period elapsed", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>True for "the instance is not there (yet)": refused, unresolved, timed out. False for login/database/TLS problems that waiting cannot fix.</summary>
        public static bool IsTransient(Exception ex) => ex is SqlException se && Classify(se) != null;

        public NodeConnection Connect(NodeInfo node)
        {
            _log.Info("Connecting to the SQL instance of " + node.VmName);
            try
            {
                var c = Open(node.ConnectionString);
                _log.Info("Connected to " + c.DataSource);
                return new NodeConnection(ConnectStatus.Connected, new SqlNode(c, _log, _releaseTimeoutSeconds));
            }
            catch (SqlException ex)
            {
                var status = Classify(ex);
                if (status == null) throw;   // login failed, database not accessible, TLS... a configuration problem: abort
                _log.Warn("SQL Server on " + node.VmName + " does not answer (" + status + "): " + ex.Message);
                return new NodeConnection(status.Value, null, "SQL error " + ex.Number + ": " + ex.Message);
            }
        }

        private sealed class SqlNode : ISqlNode
        {
            private readonly SqlConnection _c; private readonly ILog _log; private readonly int _timeout;
            public SqlNode(SqlConnection c, ILog log, int timeout) { _c = c; _log = log; _timeout = timeout; }

            public void ReleaseDatabases(IReadOnlyList<ManagedDatabase> databases)
            {
                foreach (var db in databases)
                {
                    bool? published;
                    try
                    {
                        using (var cmd = Cmd(_c, null, "SELECT is_published FROM sys.databases WHERE name = @n", Math.Min(_timeout, 60), PS("@n", db.Name)))
                        {
                            var v = cmd.ExecuteScalar();
                            published = v == null || v is DBNull ? (bool?)null : Convert.ToInt32(v) != 0;
                        }
                    }
                    catch (SqlException ex) when (IsTimeout(ex)) { throw new NodeUnresponsiveException("query on [" + db.Name + "] timed out", ex); }
                    if (published == null) { _log.Info("Database [" + db.Name + "] is not present on the previous owner, nothing to release"); continue; }
                    var sql = published.Value ? AttachScriptBuilder.BuildOffline(db.Name) : AttachScriptBuilder.BuildDetach(db.Name);
                    _log.Info((published.Value ? "Taking offline published database [" : "Detaching database [") + db.Name + "]");
                    try { using (var cmd = Cmd(_c, null, sql, _timeout)) cmd.ExecuteNonQuery(); }
                    catch (SqlException ex) when (IsTimeout(ex)) { throw new NodeUnresponsiveException("release of [" + db.Name + "] timed out after " + _timeout + "s", ex); }
                }
            }

            public void Dispose() { _c.Dispose(); }
        }

        private static bool IsTimeout(SqlException ex) => ex.Number == -2 || ex.Number == 258 || (ex.Message ?? "").IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 || (ex.Message ?? "").IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
