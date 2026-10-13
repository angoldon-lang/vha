using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace Vha.Infrastructure.Sql
{
    internal static class SqlHelper
    {
        public static SqlConnection Open(string connectionString)
        {
            var c = new SqlConnection(connectionString);
            try { c.Open(); return c; }
            catch { c.Dispose(); throw; }
        }

        public static string LocalConnectionString(string instance, string options, int timeoutSeconds = 15)
        {
            var cs = "Server=" + instance + ";Database=Jelena;Trusted_Connection=Yes;Connection Timeout=" + timeoutSeconds + ";Application Name=Jelena";
            return string.IsNullOrEmpty(options) ? cs : cs + ";" + options;
        }

        public static SqlCommand Cmd(SqlConnection c, SqlTransaction t, string sql, int timeout = 60, params SqlParameter[] p)
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeout;
            cmd.Transaction = t;
            if (p != null && p.Length > 0) cmd.Parameters.AddRange(p);
            return cmd;
        }

        public static SqlParameter P(string name, object value) => new SqlParameter(name, value ?? DBNull.Value);
        public static SqlParameter PS(string name, string value) => new SqlParameter(name, SqlDbType.NVarChar, 4000) { Value = (object)value ?? DBNull.Value };

        public static string Str(IDataRecord r, string col)
        {
            int i = Ordinal(r, col);
            return i < 0 || r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
        }

        public static int Int(IDataRecord r, string col, int def = 0)
        {
            int i = Ordinal(r, col);
            return i < 0 || r.IsDBNull(i) ? def : Convert.ToInt32(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
        }

        public static double Dbl(IDataRecord r, string col)
        {
            int i = Ordinal(r, col);
            return i < 0 || r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool Bool(IDataRecord r, string col)
        {
            int i = Ordinal(r, col);
            if (i < 0 || r.IsDBNull(i)) return false;
            var v = r.GetValue(i);
            if (v is bool b) return b;
            return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) != 0;
        }

        private static int Ordinal(IDataRecord r, string col)
        {
            for (int i = 0; i < r.FieldCount; i++)
                if (string.Equals(r.GetName(i), col, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }
}
