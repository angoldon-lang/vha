using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Sql;
using static Vha.Infrastructure.Sql.SqlHelper;

namespace Vha.Infrastructure.Sql
{
    /// <summary>Brings the managed databases online on this node. One failing database does not prevent the others from coming up.</summary>
    public sealed class LocalSql : ILocalSql
    {
        private readonly string _instance, _options; private readonly int _agId; private readonly SqlConfigStore _store; private readonly ILog _log;

        public LocalSql(string instance, string options, int agId, SqlConfigStore store, ILog log)
        { _instance = instance; _options = options; _agId = agId; _store = store; _log = log; }

        public void BringOnline(IReadOnlyList<ManagedDatabase> databases)
        {
            var failures = new List<string>();
            using (var c = Open(LocalConnectionString(_instance, _options, 30)))
            {
                foreach (var db in databases)
                {
                    try { BringOne(c, db); }
                    catch (Exception ex)
                    {
                        _log.Error("Database [" + db.Name + "] could not be brought online: " + ex.Message);
                        failures.Add(db.Name + ": " + ex.Message);
                    }
                }
            }
            if (failures.Count > 0) throw new FailoverException(failures.Count + " database(s) not online: " + string.Join(" | ", failures));
        }

        private void BringOne(SqlConnection c, ManagedDatabase db)
        {
            DbState? state = null;
            using (var cmd = Cmd(c, null, "SELECT state FROM sys.databases WHERE name = @n", 30, PS("@n", db.Name)))
            {
                var v = cmd.ExecuteScalar();
                if (v != null && !(v is DBNull)) state = (DbState)Convert.ToInt32(v);
            }
            var files = _store.GetDatabaseFiles(_agId, db.Name);
            var plan = DbOnlinePlanner.Plan(state.HasValue, state, files.Count > 0);
            _log.Info("Database [" + db.Name + "]: " + plan.Action + " (" + plan.Reason + ")");
            switch (plan.Action)
            {
                case DbAction.None: return;
                case DbAction.Fail: throw new FailoverException(plan.Reason);
                case DbAction.Attach:
                    foreach (var f in files) FileAcl.Restore(f.PhysicalName, _log);
                    using (var cmd = Cmd(c, null, AttachScriptBuilder.BuildAttach(db.Name, files), 600)) cmd.ExecuteNonQuery();
                    break;
                case DbAction.SetOnline:
                    foreach (var f in files) FileAcl.Restore(f.PhysicalName, _log);
                    using (var cmd = Cmd(c, null, AttachScriptBuilder.BuildSetOnline(db.Name), 600)) cmd.ExecuteNonQuery();
                    break;
            }
            using (var cmd = Cmd(c, null, "SELECT state FROM sys.databases WHERE name = @n", 30, PS("@n", db.Name)))
            {
                var v = cmd.ExecuteScalar();
                var now = v == null || v is DBNull ? (int?)null : Convert.ToInt32(v);
                if (now != (int)DbState.Online && now != (int)DbState.Recovering)
                    throw new FailoverException("state after " + plan.Action + " is " + (now.HasValue ? ((DbState)now.Value).ToString() : "missing"));
            }
        }
    }
}
