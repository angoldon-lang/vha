using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vha.Core.Csu;
using Vha.Core.Model;
using Vha.Core.Sql;
using static Vha.Infrastructure.Sql.SqlHelper;

namespace Vha.Infrastructure.Csu
{
    public sealed class SqlLocalDatabases : ILocalDatabases
    {
        private readonly string _instance, _options;
        public SqlLocalDatabases(string instance, string options = "") { _instance = instance; _options = options; }

        public IReadOnlyList<LocalDatabase> ListInactive()
        {
            var result = new List<LocalDatabase>();
            using (var c = Open(LocalConnectionString(_instance, _options, 10)))
            {
                var dbs = new List<KeyValuePair<string, int>>();
                using (var cmd = Cmd(c, null, "SELECT name, state FROM sys.databases WHERE state IN (3, 6)", 30))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) dbs.Add(new KeyValuePair<string, int>(Str(r, "name"), Int(r, "state")));

                foreach (var d in dbs)
                {
                    var files = new List<FileProbe>();
                    using (var cmd = Cmd(c, null, "SELECT physical_name FROM sys.master_files WHERE database_id = DB_ID(@n)", 30, PS("@n", d.Key)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var p = Str(r, "physical_name");
                            files.Add(new FileProbe { Path = p, Exists = Present(p) });
                        }
                    result.Add(new LocalDatabase { Name = d.Key, State = (DbState)d.Value, Files = files });
                }
            }
            return result;
        }

        /// <summary>When in doubt (access denied, I/O error) the file is reported as present so that nothing is dropped.</summary>
        private static bool Present(string path)
        {
            try { return File.Exists(path); }
            catch (Exception) { return true; }
        }

        public void Drop(string name)
        {
            using (var c = Open(LocalConnectionString(_instance, _options, 10)))
            using (var cmd = Cmd(c, null, "DROP DATABASE " + SqlText.QuoteName(name), 120)) cmd.ExecuteNonQuery();
        }
    }
}
