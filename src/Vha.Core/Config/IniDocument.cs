using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Vha.Core.Config
{
    /// <summary>
    /// Minimal INI reader/editor that preserves comments, order and unknown content
    /// (the disksign.ini file is edited in place). Section and key names are case-insensitive.
    /// </summary>
    public sealed class IniDocument
    {
        private readonly List<string> _lines;

        private IniDocument(List<string> lines) { _lines = lines; }

        public static IniDocument Parse(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return new IniDocument(lines);
        }

        public static IniDocument Load(string path) => Parse(File.ReadAllText(path));

        public static IniDocument Empty() => new IniDocument(new List<string>());

        private static bool IsSection(string line, out string name)
        {
            var t = line.Trim();
            if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']') { name = t.Substring(1, t.Length - 2).Trim(); return true; }
            name = null; return false;
        }

        private static bool IsKey(string line, out string key, out string value)
        {
            key = value = null;
            var t = line.Trim();
            if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') return false;
            var i = t.IndexOf('=');
            if (i <= 0) return false;
            key = t.Substring(0, i).Trim();
            value = t.Substring(i + 1).Trim();
            return true;
        }

        private bool Locate(string section, string key, out int index, out int sectionEnd)
        {
            index = -1; sectionEnd = -1;
            bool inSection = false;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (IsSection(_lines[i], out var s))
                {
                    if (inSection) { sectionEnd = i; return index >= 0; }
                    inSection = string.Equals(s, section, StringComparison.OrdinalIgnoreCase);
                    if (inSection) sectionEnd = _lines.Count;
                    continue;
                }
                if (inSection && IsKey(_lines[i], out var k, out _) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    index = i;
            }
            return index >= 0;
        }

        /// <summary>Returns the value, or an empty string when section or key do not exist.</summary>
        public string Get(string section, string key)
        {
            if (!Locate(section, key, out var idx, out _)) return "";
            IsKey(_lines[idx], out _, out var v);
            return v ?? "";
        }

        public bool HasSection(string section) => _lines.Any(l => IsSection(l, out var s) && string.Equals(s, section, StringComparison.OrdinalIgnoreCase));

        public void Set(string section, string key, string value)
        {
            if (Locate(section, key, out var idx, out _)) { _lines[idx] = key + "=" + value; return; }
            if (!HasSection(section))
            {
                if (_lines.Count > 0 && _lines[_lines.Count - 1].Trim().Length > 0) _lines.Add("");
                _lines.Add("[" + section + "]");
                _lines.Add(key + "=" + value);
                return;
            }
            // append at the end of the section (before trailing blank lines)
            int start = _lines.FindIndex(l => IsSection(l, out var s) && string.Equals(s, section, StringComparison.OrdinalIgnoreCase));
            int end = _lines.Count;
            for (int i = start + 1; i < _lines.Count; i++) if (IsSection(_lines[i], out _)) { end = i; break; }
            while (end - 1 > start && _lines[end - 1].Trim().Length == 0) end--;
            _lines.Insert(end, key + "=" + value);
        }

        public override string ToString() => string.Join("\r\n", _lines) + "\r\n";

        /// <summary>Writes through a temporary file so that a crash cannot leave a half written ini.</summary>
        public void Save(string path)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
    }
}
