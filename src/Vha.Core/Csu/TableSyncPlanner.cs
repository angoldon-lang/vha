using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vha.Core.Sql;

namespace Vha.Core.Csu
{
    /// <summary>Generates parameterized SQL to replicate the configuration tables from the peer with the highest SCN.</summary>
    public static class TableSyncPlanner
    {
        /// <summary>Tables replicated between the nodes, in dependency order.</summary>
        public static readonly string[] Tables = { "JAGroups", "JDisks", "JDatabases", "JDBFileGroups", "JDBFiles" };

        public static string FilterColumn(string table) => string.Equals(table, "JAGroups", StringComparison.OrdinalIgnoreCase) ? "ID" : "JAGroupsID";

        /// <summary>Columns that are never copied: node local state.</summary>
        public static bool IsExcluded(string table, string column)
        {
            if (string.Equals(table, "JAGroups", StringComparison.OrdinalIgnoreCase))
                return Eq(column, "ID") || Eq(column, "Is_CSU_Working");
            if (string.Equals(table, "JDisks", StringComparison.OrdinalIgnoreCase))
                return Eq(column, "Attached");
            return false;
        }

        public static void EnsureKnownTable(string table)
        {
            if (!Tables.Contains(table, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Table " + table + " is not part of the replicated set", nameof(table));
        }

        public static string BuildSelect(string table)
        {
            EnsureKnownTable(table);
            return "SELECT * FROM [dbo]." + SqlText.QuoteName(table) + " WHERE " + SqlText.QuoteName(FilterColumn(table)) + " = @agId";
        }

        public static string BuildDelete(string table)
        {
            EnsureKnownTable(table);
            if (Eq(table, "JAGroups")) throw new InvalidOperationException("JAGroups is updated, never deleted");
            return "DELETE FROM [dbo]." + SqlText.QuoteName(table) + " WHERE [JAGroupsID] = @agId";
        }

        /// <summary>UPDATE for JAGroups (parameters @p0..@pn follow the order of the returned columns), INSERT for the others.</summary>
        public static string BuildWrite(string table, IEnumerable<string> allColumns, out List<string> usedColumns)
        {
            EnsureKnownTable(table);
            usedColumns = allColumns.Where(c => !IsExcluded(table, c)).ToList();
            if (usedColumns.Count == 0) throw new InvalidOperationException("No column left to write for " + table);
            var sb = new StringBuilder();
            if (Eq(table, "JAGroups"))
            {
                sb.Append("UPDATE [dbo].[JAGroups] SET ");
                sb.Append(string.Join(", ", usedColumns.Select((c, i) => SqlText.QuoteName(c) + " = @p" + i)));
                sb.Append(" WHERE [ID] = @agId");
            }
            else
            {
                sb.Append("INSERT [dbo].").Append(SqlText.QuoteName(table)).Append(" (");
                sb.Append(string.Join(", ", usedColumns.Select(SqlText.QuoteName)));
                sb.Append(") VALUES (");
                sb.Append(string.Join(", ", usedColumns.Select((c, i) => "@p" + i)));
                sb.Append(")");
            }
            return sb.ToString();
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
