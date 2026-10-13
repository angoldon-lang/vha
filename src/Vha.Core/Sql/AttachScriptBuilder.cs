using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vha.Core.Model;

namespace Vha.Core.Sql
{
    public static class AttachScriptBuilder
    {
        /// <summary>
        /// CREATE DATABASE ... FOR ATTACH using every file described in JDBFiles
        /// (primary data file first, then the remaining files ordered by file_id).
        /// </summary>
        public static string BuildAttach(string dbName, IEnumerable<DatabaseFile> files)
        {
            var list = (files ?? throw new ArgumentNullException(nameof(files))).OrderBy(f => f.FileId).ToList();
            if (list.Count == 0) throw new ArgumentException("Database " + dbName + " has no files described", nameof(files));
            if (list[0].FileId != 1) throw new ArgumentException("Database " + dbName + ": primary data file (file_id 1) is missing from the description", nameof(files));
            if (list.Select(f => f.FileId).Distinct().Count() != list.Count) throw new ArgumentException("Database " + dbName + ": duplicated file_id", nameof(files));

            var sb = new StringBuilder();
            sb.Append("CREATE DATABASE ").Append(SqlText.QuoteName(dbName)).Append(" ON ");
            for (int i = 0; i < list.Count; i++)
            {
                var path = (list[i].PhysicalName ?? "").Trim();
                if (path.Length == 0) throw new ArgumentException("Database " + dbName + ": empty physical name for file_id " + list[i].FileId, nameof(files));
                sb.Append("(FILENAME = ").Append(SqlText.Literal(path)).Append(')');
                sb.Append(i < list.Count - 1 ? ",\r\n" : "\r\n");
            }
            sb.Append("FOR ATTACH");
            return sb.ToString();
        }

        /// <summary>Batch used on the node that gives the database away (non published databases).</summary>
        public static string BuildDetach(string dbName)
        {
            var q = SqlText.QuoteName(dbName);
            var l = SqlText.Literal(dbName);
            return "IF EXISTS (SELECT 1 FROM sys.databases WHERE name = " + l + ")\r\nBEGIN\r\n"
                 + "  ALTER DATABASE " + q + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\r\n"
                 + "  EXEC master.dbo.sp_detach_db @dbname = " + l + ";\r\nEND";
        }

        /// <summary>Batch used for databases that are published (replication): they cannot be detached.</summary>
        public static string BuildOffline(string dbName)
        {
            return "IF EXISTS (SELECT 1 FROM sys.databases WHERE name = " + SqlText.Literal(dbName) + ")\r\n"
                 + "  ALTER DATABASE " + SqlText.QuoteName(dbName) + " SET OFFLINE WITH ROLLBACK IMMEDIATE";
        }

        public static string BuildSetOnline(string dbName)
        {
            return "ALTER DATABASE " + SqlText.QuoteName(dbName) + " SET ONLINE";
        }
    }
}
