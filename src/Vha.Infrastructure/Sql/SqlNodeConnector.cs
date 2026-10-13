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

        // network level errors: the instance (or the machine) is not there
        private static readonly HashSet<int> Unavailable = new HashSet<int> { -1, 2, 53, 40, 233, 10053, 10054, 10060, 10061, 11001, 64 };

        public NodeConnection Connect(NodeInfo node)
        {
            _log.Info("Connecting to the SQL instance of " + node.VmName);
            try
            {
                var c = Open(node.ConnectionString);
                _log.Info("Connected to " + c.DataSource);
                return new NodeConnection(ConnectStatus.Connected, new SqlNode(c, _log));
            }
            catch (SqlException ex) when (ex.Number == -2 || ex.Number == 258)
            {
                return new NodeConnection(ConnectStatus.Timeout, null, ex.Message);
            }
            catch (SqlException ex) when (Unavailable.Contains(ex.Number))
            {
                return new NodeConnection(ConnectStatus.NotAvailable, null, "SQL error " + ex.Number + ": " + ex.Message);
            }
            // anything else (login failed, wrong connection string...) is a configuration problem and aborts the failover
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
