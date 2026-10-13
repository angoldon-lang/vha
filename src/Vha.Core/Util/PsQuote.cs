using System;
using System.Text;

namespace Vha.Core.Util
{
    public static class PsQuote
    {
        /// <summary>
        /// Single-quoted PowerShell literal. PowerShell also treats the typographic quotes
        /// U+2018 U+2019 U+201A U+201B as single quotes, so they are doubled as well.
        /// </summary>
        public static string Single(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("Value contains NUL", nameof(value));
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('\'');
            foreach (var c in value)
            {
                sb.Append(c);
                if (c == '\'' || c == '‘' || c == '’' || c == '‚' || c == '‛') sb.Append(c);
            }
            sb.Append('\'');
            return sb.ToString();
        }
    }
}
