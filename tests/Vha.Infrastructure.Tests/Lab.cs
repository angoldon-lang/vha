using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Vha.Infrastructure.Tests
{
    /// <summary>
    /// Two real SQL Server instances ("node A" on port 14331, "node B" on 14332) that share the directory /tmp/vhashared,
    /// mounted at the same path inside both containers and on the test host. The shared directory plays the role of the
    /// VMDK disks that move between the nodes. See tests/integration/README.md.
    /// </summary>
    public static class Lab
    {
        public const string Password = "Vha_Test#2026x";
        public const string Shared = "/tmp/vhashared";

        public static string ConnA(string db = "master") => Conn(14331, db);
        public static string ConnB(string db = "master") => Conn(14332, db);
        public static string Conn(int port, string db, string user = "sa", string pwd = Password) =>
            "Server=localhost," + port + ";Database=" + db + ";User Id=" + user + ";Password=" + pwd + ";TrustServerCertificate=True;Encrypt=True;Connection Timeout=15";

        public static readonly bool Available = Probe();

        private static bool Probe()
        {
            try { using (var c = new SqlConnection(ConnA())) { c.Open(); } using (var c = new SqlConnection(ConnB())) { c.Open(); } return Directory.Exists(Shared); }
            catch (Exception) { return false; }
        }

        public static string RepoRoot
        {
            get
            {
                var d = new DirectoryInfo(AppContext.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "vha.sln"))) d = d.Parent;
                return d?.FullName ?? throw new InvalidOperationException("vha.sln not found");
            }
        }

        public static string Sql(string name) => File.ReadAllText(Path.Combine(RepoRoot, "sql", name));

        public static void Exec(string conn, string sql, params (string, object)[] p)
        {
            using (var c = new SqlConnection(conn)) { c.Open(); using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; cmd.CommandTimeout = 120; foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value); cmd.ExecuteNonQuery(); } }
        }

        public static object Scalar(string conn, string sql, params (string, object)[] p)
        {
            using (var c = new SqlConnection(conn)) { c.Open(); using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value); var r = cmd.ExecuteScalar(); return r is DBNull ? null : r; } }
        }

        public static List<string[]> Rows(string conn, string sql)
        {
            var list = new List<string[]>();
            using (var c = new SqlConnection(conn)) { c.Open(); using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i))).ToArray()); } }
            return list;
        }

        /// <summary>Runs a script file the way sqlcmd would: $(Var) substitution and GO batch separators.</summary>
        public static void RunScript(string conn, string file, IDictionary<string, string> vars = null)
        {
            var text = Sql(file);
            if (vars != null) foreach (var kv in vars) text = text.Replace("$(" + kv.Key + ")", kv.Value);
            foreach (var batch in Regex.Split(text, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                using (var c = new SqlConnection(conn)) { c.Open(); using (var cmd = c.CreateCommand()) { cmd.CommandText = batch; cmd.CommandTimeout = 120; cmd.ExecuteNonQuery(); } }
            }
        }

        public static string Dir(string name)
        {
            var p = Path.Combine(Shared, name);
            foreach (var d in new[] { p, Path.Combine(p, "data"), Path.Combine(p, "log") })
            {
                Directory.CreateDirectory(d);
                File.SetUnixFileMode(d, (UnixFileMode)0x1FF);   // rwxrwxrwx: the mssql user of the container must be able to write
            }
            return p;
        }

        /// <summary>Empty both instances, recreate Jelena with the real schema script and register AG01 on both nodes.</summary>
        public static void Reset(bool registerAg = true)
        {
            foreach (var conn in new[] { ConnA(), ConnB() })
            {
                foreach (var row in Rows(conn, "SELECT name FROM sys.databases WHERE database_id > 4"))
                {
                    var q = "[" + row[0].Replace("]", "]]") + "]";
                    try { Exec(conn, "ALTER DATABASE " + q + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE"); } catch (SqlException) { /* offline / missing files */ }
                    Exec(conn, "DROP DATABASE " + q);
                }
                Exec(conn, "CREATE DATABASE Jelena");
            }
            foreach (var d in Directory.GetDirectories(Shared)) { try { Directory.Delete(d, true); } catch (Exception) { } }
            RunScript(ConnA("Jelena"), "01_schema.sql");
            RunScript(ConnB("Jelena"), "01_schema.sql");
            if (!registerAg) return;
            var nodes = "VM1|vm1.local|localhost,14331;VM2|vm2.local|localhost,14332";
            RunScript(ConnA("Jelena"), "02_register_availability_group.sql", new Dictionary<string, string> { { "AgName", "AG01" }, { "VmName", "VM1" }, { "NodeList", nodes }, { "Owner", "VM1" }, { "CpuMHz", "0" }, { "MemMB", "0" } });
            RunScript(ConnB("Jelena"), "02_register_availability_group.sql", new Dictionary<string, string> { { "AgName", "AG01" }, { "VmName", "VM2" }, { "NodeList", nodes }, { "Owner", "VM1" }, { "CpuMHz", "0" }, { "MemMB", "0" } });
            // the generated connection strings use integrated security; the lab uses SQL authentication
            Exec(ConnA("Jelena"), "UPDATE dbo.JNodes SET ConnectionString = CASE VMName WHEN 'VM1' THEN @a ELSE @b END", ("@a", ConnA("Jelena")), ("@b", ConnB("Jelena")));
            Exec(ConnB("Jelena"), "UPDATE dbo.JNodes SET ConnectionString = CASE VMName WHEN 'VM1' THEN @a ELSE @b END", ("@a", ConnA("Jelena")), ("@b", ConnB("Jelena")));
        }
    }

    public class SqlFactAttribute : FactAttribute
    {
        public SqlFactAttribute() { if (!Lab.Available) Skip = "SQL Server lab not reachable (see tests/integration/README.md)"; }
    }
}
