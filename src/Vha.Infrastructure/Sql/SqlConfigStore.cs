using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using Vha.Core.Failover;
using Vha.Core.Model;
using static Vha.Infrastructure.Sql.SqlHelper;

namespace Vha.Infrastructure.Sql
{
    /// <summary>Jelena configuration database of the local instance (service side). Every statement is parameterized.</summary>
    public sealed class SqlConfigStore : IConfigStore, IDisposable
    {
        private readonly SqlConnection _c;

        public SqlConfigStore(string instance, string options) { _c = Open(LocalConnectionString(instance, options)); }

        public string GetSetting(string name)
        {
            using (var cmd = Cmd(_c, null, "SELECT [SettingValue] FROM dbo.JSettings WHERE [SettingName] = @n", 30, P("@n", name)))
            {
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? null : Convert.ToString(v);
            }
        }

        public AgInfo GetAg(string name)
        {
            using (var cmd = Cmd(_c, null, "SELECT * FROM dbo.JAGroups WHERE [Name] = @n", 30, PS("@n", name)))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return null;
                return new AgInfo { Id = Int(r, "ID"), Name = Str(r, "Name"), CurrentOwner = Str(r, "CurrentOwner"), ResCpuMHz = Int(r, "ResCpuMHz"), ResMemMB = Int(r, "ResMemMB") };
            }
        }

        public IReadOnlyList<NodeInfo> GetNodes(int agId)
        {
            var list = new List<NodeInfo>();
            using (var cmd = Cmd(_c, null, "SELECT VMName, DNSName, ConnectionString, is_local FROM dbo.JNodes WHERE JAGroupsID = @id", 30, P("@id", agId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new NodeInfo { VmName = Str(r, "VMName"), DnsName = Str(r, "DNSName"), ConnectionString = Str(r, "ConnectionString"), IsLocal = Bool(r, "is_local") });
            return list;
        }

        public IReadOnlyList<ConfiguredDisk> GetDisks(int agId)
        {
            var list = new List<ConfiguredDisk>();
            using (var cmd = Cmd(_c, null, "SELECT FileName, DeviceKey, UniqueId, CapacityGB, Attached FROM dbo.JDisks WHERE JAGroupsID = @id", 30, P("@id", agId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new ConfiguredDisk { FileName = Str(r, "FileName"), DeviceKey = (Str(r, "DeviceKey") ?? "").Trim(), UniqueId = (Str(r, "UniqueId") ?? "").Trim(), CapacityGB = Dbl(r, "CapacityGB"), Attached = Bool(r, "Attached") });
            return list;
        }

        public IReadOnlyList<ManagedDatabase> GetDatabases(int agId)
        {
            var list = new List<ManagedDatabase>();
            using (var cmd = Cmd(_c, null, "SELECT DBName, PrimaryFile FROM dbo.JDatabases WHERE JAGroupsID = @id", 30, P("@id", agId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new ManagedDatabase { Name = Str(r, "DBName"), PrimaryFile = Str(r, "PrimaryFile") });
            return list;
        }

        public int GetLastScn(int agId)
        {
            using (var cmd = Cmd(_c, null, "SELECT MAX(LastSCN) FROM dbo.SCN WHERE JAGroupsID = @id", 30, P("@id", agId)))
            {
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? 0 : Convert.ToInt32(v);
            }
        }

        public void SetOwner(int agId, string owner)
        {
            using (var cmd = Cmd(_c, null, "UPDATE dbo.JAGroups SET CurrentOwner = @o WHERE ID = @id", 30, PS("@o", owner), P("@id", agId)))
            {
                var n = cmd.ExecuteNonQuery();
                if (n != 1) throw new InvalidOperationException("Updating JAGroups.CurrentOwner changed " + n + " rows, 1 expected");
            }
        }

        public void UpdateDisk(int agId, ConfiguredDisk disk, string fileName, bool attached)
        {
            using (var cmd = Cmd(_c, null, "UPDATE dbo.JDisks SET Attached = @a, FileName = @f WHERE JAGroupsID = @id AND DeviceKey = @k", 30,
                P("@a", attached), PS("@f", fileName), P("@id", agId), PS("@k", disk.DeviceKey)))
            {
                var n = cmd.ExecuteNonQuery();
                if (n != 1) throw new InvalidOperationException("Updating JDisks for device key " + disk.DeviceKey + " changed " + n + " rows, 1 expected");
            }
        }

        public void WriteScn(int agId, int scn)
        {
            using (var cmd = Cmd(_c, null, "IF NOT EXISTS (SELECT 1 FROM dbo.SCN WHERE JAGroupsID = @id AND LastSCN = @s) INSERT dbo.SCN (JAGroupsID, LastSCN) VALUES (@id, @s)", 30, P("@id", agId), P("@s", scn)))
            {
                var n = cmd.ExecuteNonQuery();
                if (n != 1) throw new InvalidOperationException("SCN " + scn + " was not inserted (already present: concurrent writer?)");
            }
        }

        /// <summary>Databases already described in JDBFiles, used by the attach logic.</summary>
        public List<DatabaseFile> GetDatabaseFiles(int agId, string dbName)
        {
            var list = new List<DatabaseFile>();
            using (var cmd = Cmd(_c, null, "SELECT file_id, LogicalName, PhysicalName, JDBFileGroupsID FROM dbo.JDBFiles WHERE JAGroupsID = @id AND DBName = @n ORDER BY file_id", 30, P("@id", agId), PS("@n", dbName)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new DatabaseFile { FileId = Int(r, "file_id"), LogicalName = Str(r, "LogicalName"), PhysicalName = (Str(r, "PhysicalName") ?? "").Trim(), FileGroupId = Int(r, "JDBFileGroupsID") });
            return list;
        }

        public void Dispose() { _c.Dispose(); }
    }
}
