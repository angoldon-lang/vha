using System;
using System.Collections.Generic;
using System.Text;

namespace Vha.Core.Vmware
{
    /// <summary>
    /// Structured form of the "Connect-VIServer ..." text stored in JSettings.ConnectVCenter.
    /// The old service executed that text as a script; now it is parsed, validated (single cmdlet, known
    /// parameters only) and executed with the values passed as variables, so no text can be injected.
    /// </summary>
    public sealed class VCenterConnectionSpec
    {
        public string Server { get; private set; }
        public string User { get; private set; }
        public string Password { get; private set; }
        public bool Force { get; private set; }
        public List<string> Warnings { get; } = new List<string>();

        public string PasswordPrefixProtected => "dpapi:";

        public static VCenterConnectionSpec Parse(string text, Func<string, string> unprotect = null)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("ConnectVCenter setting is empty");
            var tokens = Tokenize(text.Trim());
            if (tokens.Count == 0 || !string.Equals(tokens[0].Value, "Connect-VIServer", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("ConnectVCenter must start with Connect-VIServer");

            var spec = new VCenterConnectionSpec();
            for (int i = 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Value.StartsWith("-", StringComparison.Ordinal) && !t.Quoted)
                {
                    var name = t.Value.Substring(1).ToLowerInvariant();
                    switch (name)
                    {
                        case "server": spec.Server = Next(tokens, ref i, name); break;
                        case "user": spec.User = Next(tokens, ref i, name); break;
                        case "password":
                            spec.Password = Next(tokens, ref i, name);
                            if (spec.Password.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
                            {
                                if (unprotect == null) throw new FormatException("Password is DPAPI protected but no decryptor is available");
                                spec.Password = unprotect(spec.Password.Substring(6));
                            }
                            break;
                        case "protocol":
                            var p = Next(tokens, ref i, name);
                            if (!string.Equals(p, "https", StringComparison.OrdinalIgnoreCase))
                                spec.Warnings.Add("-Protocol " + p + " ignored: vCenter 6.5+ only accepts https and credentials must never travel in clear text");
                            break;
                        case "force": spec.Force = true; break;
                        default: throw new FormatException("Parameter -" + name + " is not allowed in ConnectVCenter");
                    }
                }
                else if (spec.Server == null) spec.Server = t.Value;
                else throw new FormatException("Unexpected token '" + t.Value + "' in ConnectVCenter");
            }
            if (string.IsNullOrWhiteSpace(spec.Server)) throw new FormatException("ConnectVCenter does not specify a server");
            if ((spec.User == null) != (spec.Password == null)) throw new FormatException("-User and -Password must be specified together");
            return spec;
        }

        /// <summary>Script to run; values must be provided through <see cref="Variables"/>.</summary>
        public string Script
        {
            get
            {
                var sb = new StringBuilder("Connect-VIServer -Server $vcServer -Protocol https -ErrorAction Stop");
                if (User != null) sb.Append(" -User $vcUser -Password $vcPassword");
                if (Force) sb.Append(" -Force");
                sb.Append(" | Out-Null");
                return sb.ToString();
            }
        }

        public IDictionary<string, object> Variables
        {
            get
            {
                var v = new Dictionary<string, object> { { "vcServer", Server } };
                if (User != null) { v["vcUser"] = User; v["vcPassword"] = Password; }
                return v;
            }
        }

        private static string Next(List<Token> tokens, ref int i, string name)
        {
            if (i + 1 >= tokens.Count) throw new FormatException("Missing value for -" + name);
            i++;
            return tokens[i].Value;
        }

        private sealed class Token { public string Value; public bool Quoted; }

        private static List<Token> Tokenize(string s)
        {
            var list = new List<Token>();
            int i = 0;
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i])) { i++; continue; }
                var sb = new StringBuilder();
                bool quoted = false;
                if (s[i] == '\'' || s[i] == '"')
                {
                    quoted = true;
                    char q = s[i++];
                    bool closed = false;
                    while (i < s.Length)
                    {
                        if (s[i] == q)
                        {
                            if (i + 1 < s.Length && s[i + 1] == q) { sb.Append(q); i += 2; continue; }
                            i++; closed = true; break;
                        }
                        if (q == '"' && (s[i] == '`' || s[i] == '$')) throw new FormatException("Escape or variable characters are not allowed inside double quotes; use single quotes");
                        sb.Append(s[i++]);
                    }
                    if (!closed) throw new FormatException("Unterminated quote in ConnectVCenter");
                    if (i < s.Length && !char.IsWhiteSpace(s[i])) throw new FormatException("Unexpected characters after closing quote");
                }
                else
                {
                    while (i < s.Length && !char.IsWhiteSpace(s[i]))
                    {
                        var c = s[i];
                        if (c == ';' || c == '|' || c == '&' || c == '`' || c == '$' || c == '(' || c == ')' || c == '{' || c == '}' || c == '\'' || c == '"')
                            throw new FormatException("Character '" + c + "' is not allowed in an unquoted ConnectVCenter value; enclose the value in single quotes");
                        sb.Append(c); i++;
                    }
                }
                list.Add(new Token { Value = sb.ToString(), Quoted = quoted });
            }
            return list;
        }
    }
}
