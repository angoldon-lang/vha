using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Setup;
using Vha.Core.Sql;
using static Vha.Infrastructure.Sql.SqlHelper;

namespace Vha.Infrastructure.Setup
{
    /// <summary>
    /// Prepares the Jelena database of ONE node for ONE availability group: database, schema, group and node registration,
    /// vCenter connection setting (password DPAPI protected), disks discovered in vCenter, Dummy database and database registration.
    /// Every step is idempotent. Runs the sql/*.sql scripts of the package through the same template engine used by the tests.
    /// </summary>
    public sealed class DatabaseSetup
    {
        private readonly string _sqlDir;
        private readonly ILog _log;
        private readonly IPasswordProtector _protector;
        private readonly Func<NodeSettings, AgSettings, string, string> _connection;
        private readonly char _sep;

        /// <param name="sqlDir">Folder with 01_schema.sql ... 05_health_check.sql.</param>
        /// <param name="connection">(node, ag, database) -> connection string. Default: integrated security to the instance of the node.</param>
        /// <param name="pathSeparator">'\\' on Windows (the only supported target); tests on Linux use '/'.</param>
        public DatabaseSetup(string sqlDir, ILog log, IPasswordProtector protector, Func<NodeSettings, AgSettings, string, string> connection = null, char pathSeparator = '\\')
        {
            _sqlDir = sqlDir; _log = log; _protector = protector; _sep = pathSeparator;
            _connection = connection ?? ((n, a, db) => ReplaceDatabase(LocalConnectionString(NodeNaming.SqlAddress(n, a), a.SqlOptions, 30), db));
        }

        private static string ReplaceDatabase(string cs, string db)
        {
            var b = new SqlConnectionStringBuilder(cs) { InitialCatalog = db };
            return b.ConnectionString;
        }

        public List<string> Run(DeploymentConfig c, AgSettings ag, NodeSettings node, string vcenterPassword, IVCenter vcenter)
        {
            var report = new List<string>();
            void Say(string m) { report.Add(m); _log.Info(m); }

            // 1. database
            using (var master = Vha.Core.Util.Retry.Run(() => Open(_connection(node, ag, "master")), 6, TimeSpan.FromSeconds(5), Vha.Infrastructure.Sql.SqlNodeConnector.IsTransient, (n, ex) => _log.Warn("SQL Server not ready (attempt " + n + "/6): " + ex.Message)))
            {
                var exists = Scalar(master, "SELECT DB_ID(N'Jelena')") != null;
                if (!exists)
                {
                    Exec(master, "CREATE DATABASE [Jelena]");
                    Exec(master, "ALTER DATABASE [Jelena] SET RECOVERY SIMPLE");
                    Say("database Jelena created on " + NodeNaming.SqlAddress(node, ag) + " (make sure the instance default data path is a LOCAL disk, never a mount point of the group)");
                }
                else Say("database Jelena already exists");
            }

            var jelena = _connection(node, ag, "Jelena");
            // 2. schema, group, nodes
            RunScript(jelena, "01_schema.sql", new Dictionary<string, string>());
            Say("schema verified");
            var nodeList = string.Join(";", c.Nodes.Select(n => n.VmName + "|" + n.DnsName + "|" + NodeNaming.SqlAddress(n, ag)));
            RunScript(jelena, "02_register_availability_group.sql", new Dictionary<string, string>
            {
                { "AgName", ag.Name }, { "VmName", node.VmName }, { "NodeList", nodeList }, { "Owner", ag.InitialOwner },
                { "CpuMHz", (ag.ReservationEnabled ? ag.ReservationCpuMHz : 0).ToString() }, { "MemMB", (ag.ReservationEnabled ? ag.ReservationMemMB : 0).ToString() },
                { "CsOptions", "Connection Timeout=16;MultipleActiveResultSets=True;" + (ag.SqlOptions ?? "").Trim().Trim(';') }
            });
            Say("availability group " + ag.Name + " and its " + c.Nodes.Count + " nodes registered (is_local = " + node.VmName + ")");

            using (var conn = Open(jelena))
            {
                var agId = Convert.ToInt32(Scalar(conn, "SELECT ID FROM dbo.JAGroups WHERE Name = @n", PS("@n", ag.Name)));

                // 3. vCenter connection setting
                var setting = ConnectVCenterSetting(c, vcenterPassword);
                Exec(conn, "MERGE dbo.JSettings AS t USING (SELECT 'ConnectVCenter' AS SettingName, @v AS SettingValue) AS s ON t.SettingName = s.SettingName "
                         + "WHEN MATCHED THEN UPDATE SET SettingValue = s.SettingValue WHEN NOT MATCHED THEN INSERT (SettingName, SettingValue) VALUES (s.SettingName, s.SettingValue);", PS("@v", setting));
                Say("vCenter connection stored in JSettings" + (string.IsNullOrWhiteSpace(c.VCenter.User) ? " (pass-through authentication)" : " (password protected with DPAPI for this machine)"));

                // 4. disks
                var byVm = new Dictionary<string, IReadOnlyList<VmDisk>>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in c.Nodes) byVm[n.VmName] = vcenter.GetDisks(n.VmName);
                var disks = DiskDiscovery.Discover(ag.DiskNameFilter, byVm, node.VmName);
                foreach (var d in disks)
                {
                    Exec(conn, "MERGE dbo.JDisks AS t USING (SELECT @id AS JAGroupsID, @k AS DeviceKey) AS s ON t.JAGroupsID = s.JAGroupsID AND t.DeviceKey = s.DeviceKey "
                             + "WHEN MATCHED THEN UPDATE SET FileName = @f, CapacityGB = @c, UniqueId = @u, Attached = @a "
                             + "WHEN NOT MATCHED THEN INSERT (JAGroupsID, DeviceKey, FileName, CapacityGB, UniqueId, Attached) VALUES (@id, @k, @f, @c, @u, @a);",
                        P("@id", agId), PS("@k", d.Disk.DeviceKey), PS("@f", d.Disk.FileName), P("@c", d.Disk.CapacityGB), PS("@u", d.Disk.UniqueId), P("@a", d.Disk.Attached));
                }
                var stale = Rows(conn, "SELECT DeviceKey FROM dbo.JDisks WHERE JAGroupsID = @id", P("@id", agId)).Where(k => disks.All(d => d.Disk.DeviceKey != k)).ToList();
                foreach (var k in stale) _log.Warn("JDisks contains device key " + k + " that no longer matches the filter '" + ag.DiskNameFilter + "': review it manually");
                Say(disks.Count + " disk(s) of " + ag.Name + " registered: " + string.Join(", ", disks.Select(d => d.Disk.DeviceKey + (d.Disk.Attached ? "*" : "")).ToArray()) + "  (* = attached to this node)");

                // 5. Dummy database + database registration: only on the node that holds the disks
                var holdsDisks = disks.Any(d => d.Disk.Attached);
                var dataDir = ag.MountRoot.TrimEnd('\\', '/') + _sep + "Data";
                var logDir = ag.MountRoot.TrimEnd('\\', '/') + _sep + "Log";
                if (!holdsDisks || !Directory.Exists(dataDir) || !Directory.Exists(logDir))
                {
                    Say("this node does not hold the disks of " + ag.Name + ": the Dummy database and the database registration are done on " + ag.InitialOwner);
                    return report;
                }
                if (ag.CreateDummyDatabase)
                {
                    var exists = Scalar(conn, "SELECT DB_ID(@n)", PS("@n", ag.DummyDatabaseName)) != null;
                    if (!exists)
                    {
                        var udata = dataDir + _sep + "UserData"; var ulog = logDir + _sep + "UserLog";
                        Directory.CreateDirectory(udata); Directory.CreateDirectory(ulog);
                        var n = SqlText.QuoteName(ag.DummyDatabaseName);
                        Exec(conn, "CREATE DATABASE " + n + " ON PRIMARY (NAME = N'Dummy', FILENAME = " + SqlText.FileLiteral(udata + _sep + ag.DummyDatabaseName + ".mdf") + ", SIZE = 51200KB, FILEGROWTH = 51200KB) "
                                 + "LOG ON (NAME = N'Dummy_log', FILENAME = " + SqlText.FileLiteral(ulog + _sep + ag.DummyDatabaseName + "_log.ldf") + ", SIZE = 10240KB, FILEGROWTH = 10240KB)");
                        Say("database " + ag.DummyDatabaseName + " created on the group disks");
                    }
                    else Say("database " + ag.DummyDatabaseName + " already exists");
                }
            }
            RunScript(jelena, "03_register_databases.sql", new Dictionary<string, string> { { "AgName", ag.Name }, { "MountRoot", ag.MountRoot.TrimEnd('\\', '/') + _sep } });
            using (var conn = Open(jelena))
            {
                var registered = Rows(conn, "SELECT DBName FROM dbo.JDatabases WHERE JAGroupsID = (SELECT ID FROM dbo.JAGroups WHERE Name = @n) ORDER BY DBName", PS("@n", ag.Name));
                Say(registered.Count + " database(s) registered for failover: " + string.Join(", ", registered.ToArray()));
                if (!registered.Any(r => string.Equals(r, ag.DummyDatabaseName, StringComparison.OrdinalIgnoreCase)) && ag.CreateDummyDatabase)
                    throw new InvalidOperationException("The Dummy database is not registered in dbo.JDatabases");
            }
            return report;
        }

        /// <summary>Text stored in JSettings.ConnectVCenter (parsed again by VCenterConnectionSpec at run time).</summary>
        public string ConnectVCenterSetting(DeploymentConfig c, string vcenterPassword)
        {
            var server = c.VCenter.Server.Trim();
            if (string.IsNullOrWhiteSpace(c.VCenter.User)) return "Connect-VIServer " + server + " -Protocol https";
            if (string.IsNullOrEmpty(vcenterPassword)) throw new InvalidOperationException("A vCenter user is configured but no password was supplied");
            return "Connect-VIServer " + server + " -User '" + c.VCenter.User.Trim().Replace("'", "''") + "' -Password " + _protector.Protect(vcenterPassword) + " -Protocol https";
        }

        private void RunScript(string connection, string file, IDictionary<string, string> vars)
        {
            var path = Path.Combine(_sqlDir, file);
            if (!File.Exists(path)) throw new FileNotFoundException("SQL script not found: " + path);
            var text = SqlScriptTemplate.Apply(File.ReadAllText(path), vars);
            using (var c = Open(connection))
                foreach (var batch in SqlScriptTemplate.Batches(text))
                    using (var cmd = c.CreateCommand()) { cmd.CommandText = batch; cmd.CommandTimeout = 120; cmd.ExecuteNonQuery(); }
        }

        private static void Exec(SqlConnection c, string sql, params SqlParameter[] p) { using (var cmd = Cmd(c, null, sql, 120, p)) cmd.ExecuteNonQuery(); }
        private static object Scalar(SqlConnection c, string sql, params SqlParameter[] p) { using (var cmd = Cmd(c, null, sql, 60, p)) { var r = cmd.ExecuteScalar(); return r is DBNull ? null : r; } }
        private static List<string> Rows(SqlConnection c, string sql, params SqlParameter[] p)
        {
            var l = new List<string>();
            using (var cmd = Cmd(c, null, sql, 60, p)) using (var r = cmd.ExecuteReader()) while (r.Read()) l.Add(Convert.ToString(r.GetValue(0)));
            return l;
        }
    }
}
