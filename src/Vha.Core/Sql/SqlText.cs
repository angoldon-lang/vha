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

        /// <summary>
        /// Returns a unicode string EXPRESSION for use in SELECT/WHERE/SET: N'value' with quotes doubled.
        /// T-SQL treats a backslash followed by a line break inside a literal as a line continuation and silently drops both, so line breaks are never
        /// emitted raw: they become NCHAR(13)/NCHAR(10) concatenations. Where only a pure literal is allowed (FILENAME = ..., EXEC parameters) use
        /// <see cref="FileLiteral"/> or assign this expression to a variable first.
        /// </summary>
        public static string Literal(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("Literal contains NUL", nameof(value));
            if (value.IndexOf('\r') < 0 && value.IndexOf('\n') < 0) return "N'" + value.Replace("'", "''") + "'";
            var sb = new StringBuilder();
            var run = new StringBuilder();
            void Flush() { if (run.Length > 0) { if (sb.Length > 0) sb.Append(" + "); sb.Append("N'").Append(run.ToString().Replace("'", "''")).Append('\''); run.Clear(); } }
            foreach (var c in value)
            {
                if (c == '\r' || c == '\n') { Flush(); if (sb.Length > 0) sb.Append(" + "); sb.Append(c == '\r' ? "NCHAR(13)" : "NCHAR(10)"); }
                else run.Append(c);
            }
            Flush();
            return "(" + sb + ")";
        }

        /// <summary>A pure string literal (no expression), for places such as FILENAME = N'...'. File names cannot contain line breaks.</summary>
        public static string FileLiteral(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("Literal contains NUL", nameof(value));
            if (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) throw new ArgumentException("A file name cannot contain line breaks", nameof(value));
            return "N'" + value.Replace("'", "''") + "'";
        }
    }
}
