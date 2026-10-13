using System;
using System.Text;

namespace Vha.Core.Util
{
    public static class ProcessArgs
    {
        /// <summary>Quotes one argument following the CommandLineToArgvW rules.</summary>
        public static string Quote(string arg)
        {
            if (arg == null) throw new ArgumentNullException(nameof(arg));
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
