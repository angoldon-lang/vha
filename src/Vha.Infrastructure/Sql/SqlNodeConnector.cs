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
        public SqlNodeConnector(ILog log) { _log = log; }

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
            if (m.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("pre-login handshake", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("SSL Provider", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;
            if (ex.Number == -2 || ex.Number == 258 || m.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                return ConnectStatus.Timeout;
            return ConnectStatus.NotAvailable;
        }

        public NodeConnection Connect(NodeInfo node)
        {
            _log.Info("Connecting to the SQL instance of " + node.VmName);
            try
            {
                var c = Open(node.ConnectionString);
                _log.Info("Connected to " + c.DataSource);
                return new NodeConnection(ConnectStatus.Connected, new SqlNode(c, _log));
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
            private readonly SqlConnection _c; private readonly ILog _log;
            public SqlNode(SqlConnection c, ILog log) { _c = c; _log = log; }

            public void ReleaseDatabases(IReadOnlyList<ManagedDatabase> databases)
            {
                foreach (var db in databases)
                {
                    bool? published;
                    using (var cmd = Cmd(_c, null, "SELECT is_published FROM sys.databases WHERE name = @n", 30, PS("@n", db.Name)))
                    {
                        var v = cmd.ExecuteScalar();
                        published = v == null || v is DBNull ? (bool?)null : Convert.ToInt32(v) != 0;
                    }
                    if (published == null) { _log.Info("Database [" + db.Name + "] is not present on the previous owner, nothing to release"); continue; }
                    var sql = published.Value ? AttachScriptBuilder.BuildOffline(db.Name) : AttachScriptBuilder.BuildDetach(db.Name);
                    _log.Info((published.Value ? "Taking offline published database [" : "Detaching database [") + db.Name + "]");
                    using (var cmd = Cmd(_c, null, sql, 300)) cmd.ExecuteNonQuery();
                }
            }

            public void Dispose() { _c.Dispose(); }
        }
    }
}
