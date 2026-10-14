using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Vha.Core.Csu;
using Vha.Core.Model;
using Vha.Core.Sql;
using static Vha.Infrastructure.Sql.SqlHelper;

namespace Vha.Infrastructure.Csu
{
    public sealed class SqlCsuStore : ICsuStore, IDisposable
    {
        private readonly SqlConnection _c;
        private SqlTransaction _tx;

        public SqlCsuStore(string instance, string options = "") { _c = Open(LocalConnectionString(instance, options, 10)); }

        private SqlCommand Q(string sql, params SqlParameter[] p) => Cmd(_c, _tx, sql, 60, p);

        public int GetAgId(string agName)
        {
            using (var cmd = Q("SELECT [ID] FROM dbo.JAGroups WHERE [Name] = @n", PS("@n", agName.Trim())))
            {
                var v = cmd.ExecuteScalar();
                if (v == null || v is DBNull) throw new InvalidOperationException("Availability group '" + agName + "' not found in dbo.JAGroups");
                return Convert.ToInt32(v);
            }
        }

        public void SetWorking(int agId, bool working)
        {
            using (var cmd = Q("UPDATE dbo.JAGroups SET [Is_CSU_Working] = @w WHERE ID = @id", PS("@w", working ? "Y" : "N"), P("@id", agId))) cmd.ExecuteNonQuery();
        }

        public void EnsureSchema()
        {
            Exec("IF OBJECT_ID(N'dbo.JDBFileGroups', N'U') IS NULL CREATE TABLE dbo.JDBFileGroups (JAGroupsID INT NOT NULL, DBName NVARCHAR(128) NOT NULL, ID INT NOT NULL, FGName NVARCHAR(100) NOT NULL, FGTypeDesc NVARCHAR(200) NOT NULL, CONSTRAINT PK_JDBFileGroups PRIMARY KEY CLUSTERED (JAGroupsID, DBName, ID))");
            Exec("IF OBJECT_ID(N'dbo.JDBFiles', N'U') IS NULL CREATE TABLE dbo.JDBFiles (JAGroupsID INT NOT NULL, file_id INT NOT NULL, DBName NVARCHAR(128) NOT NULL, JDBFileGroupsID INT NOT NULL, LogicalName NVARCHAR(100) NOT NULL, PhysicalName NVARCHAR(512) NOT NULL, CONSTRAINT PK_JDBFiles PRIMARY KEY CLUSTERED (JAGroupsID, DBName, file_id))");
            Exec("IF OBJECT_ID(N'dbo.JXLog', N'U') IS NULL CREATE TABLE dbo.JXLog (ID INT IDENTITY(1,1), JAGroupsID INT, MsgType VARCHAR(10), Message VARCHAR(1000), [date] DATETIME NOT NULL DEFAULT (GETDATE()))");
        }

        private void Exec(string sql, params SqlParameter[] p) { using (var cmd = Q(sql, p)) cmd.ExecuteNonQuery(); }

        public void DescribeDatabases(int agId)
        {
            foreach (var db in GetDatabases(agId))
            {
                using (var tx = _c.BeginTransaction())
                {
                    try
                    {
                        int state;
                        using (var cmd = Cmd(_c, tx, "SELECT state FROM sys.databases WHERE name = @n", 30, PS("@n", db.Name)))
                        {
                            var v = cmd.ExecuteScalar();
                            if (v == null || v is DBNull) { tx.Rollback(); continue; }
                            state = Convert.ToInt32(v);
                        }
                        if (state != (int)DbState.Online) { tx.Rollback(); continue; }

                        var q = SqlText.QuoteName(db.Name);
                        var groups = new DataTable(); var files = new DataTable();
                        using (var cmd = Cmd(_c, tx, "SELECT data_space_id, name, type_desc FROM " + q + ".sys.filegroups", 30)) groups.Load(cmd.ExecuteReader());
                        using (var cmd = Cmd(_c, tx, "SELECT file_id, data_space_id, name, physical_name FROM " + q + ".sys.database_files", 30)) files.Load(cmd.ExecuteReader());

                        using (var cmd = Cmd(_c, tx, "DELETE FROM dbo.JDBFiles WHERE JAGroupsID = @id AND DBName = @n", 30, P("@id", agId), PS("@n", db.Name))) cmd.ExecuteNonQuery();
                        using (var cmd = Cmd(_c, tx, "DELETE FROM dbo.JDBFileGroups WHERE JAGroupsID = @id AND DBName = @n", 30, P("@id", agId), PS("@n", db.Name))) cmd.ExecuteNonQuery();
                        foreach (DataRow g in groups.Rows)
                            using (var cmd = Cmd(_c, tx, "INSERT dbo.JDBFileGroups (JAGroupsID, DBName, ID, FGName, FGTypeDesc) VALUES (@id, @n, @i, @f, @t)", 30,
                                P("@id", agId), PS("@n", db.Name), P("@i", g["data_space_id"]), PS("@f", Convert.ToString(g["name"])), PS("@t", Convert.ToString(g["type_desc"])))) cmd.ExecuteNonQuery();
                        foreach (DataRow f in files.Rows)
                            using (var cmd = Cmd(_c, tx, "INSERT dbo.JDBFiles (JAGroupsID, DBName, file_id, JDBFileGroupsID, LogicalName, PhysicalName) VALUES (@id, @n, @fid, @gid, @l, @p)", 30,
                                P("@id", agId), PS("@n", db.Name), P("@fid", f["file_id"]), P("@gid", f["data_space_id"]), PS("@l", Convert.ToString(f["name"])), PS("@p", Convert.ToString(f["physical_name"])))) cmd.ExecuteNonQuery();
                        tx.Commit();
                    }
                    catch { try { tx.Rollback(); } catch (Exception) { } throw; }
                }
            }
        }

        public void BeginTransaction() { if (_tx != null) throw new InvalidOperationException("Transaction already open"); _tx = _c.BeginTransaction(); }
        public void Commit() { var t = _tx; _tx = null; t.Commit(); t.Dispose(); }
        public void Rollback() { var t = _tx; _tx = null; if (t != null) { try { t.Rollback(); } finally { t.Dispose(); } } }

        public int GetLastScn(int agId)
        {
            using (var cmd = Q("SELECT MAX(LastSCN) FROM dbo.SCN WHERE JAGroupsID = @id", P("@id", agId)))
            {
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? 0 : Convert.ToInt32(v);
            }
        }

        public IReadOnlyList<NodeInfo> GetRemoteNodes(int agId)
        {
            var list = new List<NodeInfo>();
            using (var cmd = Q("SELECT VMName, DNSName, ConnectionString, is_local FROM dbo.JNodes WHERE JAGroupsID = @id AND is_local = 0", P("@id", agId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new NodeInfo { VmName = Str(r, "VMName"), DnsName = Str(r, "DNSName"), ConnectionString = Str(r, "ConnectionString"), IsLocal = false });
            return list;
        }

        public void CopyFromPeer(NodeInfo peer, int agId)
        {
            if (_tx == null) throw new InvalidOperationException("CopyFromPeer must run inside the transaction");
            using (var remote = Open(peer.ConnectionString))
            {
                foreach (var table in TableSyncPlanner.Tables)
                {
                    var data = new DataTable();
                    using (var cmd = Cmd(remote, null, TableSyncPlanner.BuildSelect(table), 60, P("@agId", agId)))
                    using (var rd = cmd.ExecuteReader()) data.Load(rd);

                    var cols = data.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
                    var write = TableSyncPlanner.BuildWrite(table, cols, out var used);
                    if (table == "JAGroups")
                    {
                        if (data.Rows.Count != 1) throw new InvalidOperationException("Peer " + peer.VmName + " returned " + data.Rows.Count + " rows for JAGroups, 1 expected");
                    }
                    else
                    {
                        Exec(TableSyncPlanner.BuildDelete(table), P("@agId", agId));
                    }
                    foreach (DataRow row in data.Rows)
                    {
                        var ps = used.Select((c, i) => new SqlParameter("@p" + i, row[c] ?? DBNull.Value)).ToList();
                        ps.Add(P("@agId", agId));
                        using (var cmd = Q(write, ps.ToArray()))
                        {
                            var n = cmd.ExecuteNonQuery();
                            if (n < 1) throw new InvalidOperationException("Replicating " + table + " affected no rows");
                        }
                    }
                }
            }
        }

        public void WriteScn(int agId, int scn)
        {
            using (var cmd = Q("IF NOT EXISTS (SELECT 1 FROM dbo.SCN WHERE JAGroupsID = @id AND LastSCN = @s) INSERT dbo.SCN (JAGroupsID, LastSCN) VALUES (@id, @s)", P("@id", agId), P("@s", scn)))
            {
                var n = cmd.ExecuteNonQuery();
                if (n != 1) throw new InvalidOperationException("SCN " + scn + " was not inserted (already present)");
            }
        }

        public IReadOnlyList<ConfiguredDisk> GetDisks(int agId)
        {
            var list = new List<ConfiguredDisk>();
            using (var cmd = Q("SELECT FileName, DeviceKey, UniqueId, CapacityGB, Attached FROM dbo.JDisks WHERE JAGroupsID = @id", P("@id", agId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new ConfiguredDisk { FileName = Str(r, "FileName"), DeviceKey = (Str(r, "DeviceKey") ?? "").Trim(), UniqueId = (Str(r, "UniqueId") ?? "").Trim(), CapacityGB = Dbl(r, "CapacityGB"), Attached = Bool(r, "Attached") });
            return list;
        }

        public void ApplyDiskOperation(int agId, DiskOperation op)
        {
            string sql; SqlParameter[] p;
            switch (op.Op)
            {
                case DiskOp.Insert:
                    sql = "INSERT dbo.JDisks (Attached, JAGroupsID, FileName, DeviceKey, CapacityGB, UniqueId) VALUES (1, @id, @f, @k, @c, @u)";
                    p = new[] { P("@id", agId), PS("@f", op.FileName), PS("@k", op.DeviceKey), P("@c", op.CapacityGB), PS("@u", op.UniqueId) }; break;
                case DiskOp.Update:
                    sql = "UPDATE dbo.JDisks SET Attached = 1, FileName = @f, CapacityGB = @c WHERE JAGroupsID = @id AND DeviceKey = @k";
                    p = new[] { P("@id", agId), PS("@f", op.FileName), PS("@k", op.DeviceKey), P("@c", op.CapacityGB) }; break;
                case DiskOp.MarkDetached:
                    sql = "UPDATE dbo.JDisks SET Attached = 0 WHERE JAGroupsID = @id AND DeviceKey = @k";
                    p = new[] { P("@id", agId), PS("@k", op.DeviceKey) }; break;
                default: return;
            }
            using (var cmd = Q(sql, p))
            {
                var n = cmd.ExecuteNonQuery();
                if (n != 1) throw new InvalidOperationException("JDisks " + op.Op + " for " + op.DeviceKey + " changed " + n + " rows, 1 expected");
            }
        }

        public string GetOwner(int agId)
        {
            using (var cmd = Q("SELECT CurrentOwner FROM dbo.JAGroups WHERE ID = @id", P("@id", agId)))
            {
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? null : Convert.ToString(v);
            }
        }

        public void SetOwner(int agId, string owner)
        {
            using (var cmd = Q("UPDATE dbo.JAGroups SET CurrentOwner = @o WHERE ID = @id", PS("@o", owner), P("@id", agId)))
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Updating CurrentOwner did not change exactly one row");
        }

        public IReadOnlyList<ManagedDatabase> GetDatabases(int agId)
        {
            var list = new List<ManagedDatabase>();
            using (var cmd = Q("SELECT DBName, PrimaryFile FROM dbo.JDatabases WHERE JAGroupsID = @id", P("@id", agId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new ManagedDatabase { Name = Str(r, "DBName"), PrimaryFile = Str(r, "PrimaryFile") });
            return list;
        }

        public void Dispose() { Rollback(); _c.Dispose(); }
    }
}
