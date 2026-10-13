using System;
using System.Text;

namespace Vha.Core.Sql
{
    /// <summary>
    /// Safe building blocks for the few places where T-SQL must be generated as text
    /// (DDL cannot be parameterized). Everything else uses SqlParameter.
    /// </summary>
    public static class SqlText
    {
        /// <summary>Returns a bracket-quoted identifier: [name] with ']' doubled.</summary>
        public static string QuoteName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Identifier cannot be empty", nameof(name));
            if (name.Length > 128) throw new ArgumentException("Identifier longer than 128 characters", nameof(name));
            if (name.IndexOf('\0') >= 0) throw new ArgumentException("Identifier contains NUL", nameof(name));
            return "[" + name.Replace("]", "]]") + "]";
        }

        /// <summary>Returns a unicode string literal N'value' with quotes doubled.</summary>
        public static string Literal(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("Literal contains NUL", nameof(value));
            return "N'" + value.Replace("'", "''") + "'";
        }
    }
}
