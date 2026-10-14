using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Vha.Core.Config;
using Vha.Core.Setup;
using Vha.Core.Sql;
using Vha.Core.Tests;
using Vha.Core.Util;
using Vha.Core.Vmware;
using Vha.Infrastructure.PowerShell;
using Xunit;

namespace Vha.Infrastructure.Tests
{
    /// <summary>
    /// Randomised round-trip tests: thousands of hostile strings are pushed through the generators and evaluated by the REAL
    /// consumer (PowerShell, SQL Server). A single mismatch means an injection or a corruption path. Fixed seeds: reproducible.
    /// </summary>
    public class PropertyTests
    {
        // quotes of every kind, shell/SQL metacharacters, controls, unicode, surrogate pairs
        private static readonly string[] Atoms =
        {
            "a", "Z", "0", "9", " ", "  ", "'", "''", "\"", "`", "$", "$(", ")", "(", "{", "}", ";", "|", "&", "\\", "\\\\", "/", "<", ">", "\n", "\r\n", "\r", "\t",
            "\u2018", "\u2019", "\u201A", "\u201B", "\u201C", "\u201D", "\u201E", "\u00A0", "\u200B", "\u2028", "\u2029", "\u0085", "\u00E8", "\u00DF", "\u65E5\u672C", "\uD83D\uDE00", "[", "]", "]]", "#", "@", "%", "*", "?", "-", "--", "/*", "*/", "=", ",", ".", ":", "!", "~", "^", "\u0001", "\u001F", "\u007F",
            "N'", "'; DROP TABLE x; --", "$env:USERNAME", "`n", "@'", "'@", "<#", "#>", "%PATH%", "$(Get-Date)", "dpapi:"
        };

        private static string Random(Random r, int maxAtoms = 12, bool allowEmpty = true)
        {
            var n = r.Next(allowEmpty ? 0 : 1, maxAtoms + 1);
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(Atoms[r.Next(Atoms.Length)]);
            return sb.ToString();
        }

        private static IEnumerable<string> Samples(int seed, int count, int maxAtoms = 12, bool allowEmpty = true)
        {
            var r = new Random(seed);
            foreach (var a in Atoms) yield return a;                    // every atom alone
            for (int i = 0; i < count; i++) yield return Random(r, maxAtoms, allowEmpty);
        }

        // ------------------------------------------------------------------ PowerShell

        [Fact]
        public void PsQuote_single_is_evaluated_by_real_powershell_to_exactly_the_original_string()
        {
            using var h = new PowerShellHost(TimeSpan.FromMinutes(2));
            var all = Samples(1, 3000).Distinct().ToList();
            for (int i = 0; i < all.Count; i += 250)
            {
                var chunk = all.Skip(i).Take(250).ToList();
                var script = "@(" + string.Join(",\r\n", chunk.Select(PsQuote.Single)) + ")";
                var res = h.Invoke(script).Select(o => (string)o.BaseObject).ToList();
                Assert.Equal(chunk.Count, res.Count);
                for (int k = 0; k < chunk.Count; k++)
                    Assert.True(chunk[k] == res[k], "mismatch for " + Escape(chunk[k]) + " got " + Escape(res[k]));
            }
        }

        [Fact]
        public void A_quoted_string_never_changes_what_the_following_statement_does()
        {
            // the quoted value is followed by a canary statement: if any value escaped its quotes the canary would not be the last output
            using var h = new PowerShellHost(TimeSpan.FromMinutes(2));
            foreach (var s in Samples(2, 800).Distinct())
            {
                var res = h.Invoke("$v = " + PsQuote.Single(s) + "\r\n'canary'\r\n$v.Length").Select(o => o.ToString()).ToList();
                Assert.Equal(new[] { "canary", s.Length.ToString() }, res.ToArray());
            }
        }

        [Fact]
        public void The_vcenter_connection_passes_any_password_through_unchanged()
        {
            using var h = new PowerShellHost(TimeSpan.FromMinutes(2));
            h.Invoke("function Connect-VIServer { param([string]$Server, [string]$User, [string]$Password, [string]$Protocol, [switch]$Force, $ErrorAction) $global:Got = [pscustomobject]@{ P = $Password; U = $User; S = $Server } }");
            foreach (var pw in Samples(3, 600, allowEmpty: false).Where(p => !p.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase) && !p.Contains('\0')).Distinct())
            {
                var text = "Connect-VIServer vc.example.local -User 'EX\\u' -Password " + PsQuote.Single(pw) + " -Protocol https";
                var spec = VCenterConnectionSpec.Parse(text);
                Assert.Equal(pw, spec.Password);
                h.Invoke(spec.Script, spec.Variables);
                Assert.Equal(pw, (string)h.Invoke("$global:Got.P").Single().BaseObject);
            }
        }

        [Fact]
        public void Ps_scripts_built_from_hostile_names_parse_and_keep_the_name_as_data()
        {
            using var h = new PowerShellHost(TimeSpan.FromMinutes(2));
            h.Invoke("function Get-VM { param([string]$Name, $ErrorAction) $global:Asked = $Name; throw 'stop' }");
            foreach (var name in Samples(4, 400, allowEmpty: false).Where(n => !n.Contains('\0')).Distinct())
            {
                Assert.ThrowsAny<Exception>(() => h.Invoke(PowerCliScripts.GetVm(name)));
                Assert.Equal(name, (string)h.Invoke("$global:Asked").Single().BaseObject);
            }
        }

        // ------------------------------------------------------------------ SQL Server

        [SqlFact]
        public void SqlText_literal_and_quotename_are_read_back_unchanged_by_a_real_sql_server()
        {
            Lab.Reset(registerAg: false);
            var conn = Lab.ConnA("Jelena");
            foreach (var s in Samples(5, 1500, allowEmpty: true).Where(x => !x.Contains('\0')).Distinct())
            {
                var back = Lab.Scalar(conn, "SELECT " + SqlText.Literal(s));
                Assert.True(s == Convert.ToString(back), "literal mismatch for " + Escape(s));
            }
            foreach (var s in Samples(6, 600, maxAtoms: 8, allowEmpty: false).Where(x => !x.Contains('\0') && x.Length <= 128 && x.Trim().Length > 0).Distinct())
            {
                using var c = new SqlConnection(conn); c.Open();
                using var cmd = new SqlCommand("SELECT 1 AS " + SqlText.QuoteName(s), c);
                using var rd = cmd.ExecuteReader();
                Assert.True(s == rd.GetName(0), "identifier mismatch for " + Escape(s));
            }
        }

        [SqlFact]
        public void Script_template_values_inside_string_literals_arrive_unchanged_in_sql_server()
        {
            Lab.Reset(registerAg: false);
            var conn = Lab.ConnA("Jelena");
            foreach (var s in Samples(7, 800).Where(x => !x.Contains('\0') && !x.Contains("$(")).Distinct())   // "$(" would be a second variable reference
            {
                if (s.Contains('\r') || s.Contains('\n'))   // refused by design (T-SQL drops backslash + line break inside a literal)
                {
                    Assert.Throws<ArgumentException>(() => SqlScriptTemplate.Apply("SELECT N'$(Value)'", new Dictionary<string, string> { { "Value", s } }));
                    continue;
                }
                var sql = SqlScriptTemplate.Apply("SELECT N'$(Value)'", new Dictionary<string, string> { { "Value", s } });
                Assert.True(s == Convert.ToString(Lab.Scalar(conn, sql)), "template mismatch for " + Escape(s));
            }
        }

        [SqlFact]
        public void Databases_with_hostile_names_survive_the_full_describe_release_attach_cycle()
        {
            Lab.Reset();
            var dir = Lab.Dir("ag01");
            var r = new Random(8);
            var names = new List<string> { "a'b", "a]b", "a]]b", "[x]", "a b", "a;b", "--c", "x'; DROP DATABASE Jelena; --", "\u65E5\u672C\u8A9E", "\u00E9", "a\"b", "a&b", "a%b", "a$b", "N'x'" };
            int i = 0;
            foreach (var name in names)
            {
                var file = "db" + (i++);
                var q = "[" + name.Replace("]", "]]") + "]";
                Lab.Exec(Lab.ConnA(), "CREATE DATABASE " + q + " ON (NAME=n" + file + ", FILENAME=N'" + dir + "/data/" + file + ".mdf') LOG ON (NAME=l" + file + ", FILENAME=N'" + dir + "/log/" + file + ".ldf')");
                Lab.Exec(Lab.ConnA(), "USE " + q + "; CREATE TABLE t (v INT); INSERT t VALUES (42)");
                Lab.Exec(Lab.ConnA("Jelena"), "INSERT dbo.JDatabases (JAGroupsID, DBName, PrimaryFile) VALUES (1, @n, @p)", ("@n", name), ("@p", dir + "/data/" + file + ".mdf"));
            }
            new Vha.Infrastructure.Csu.SqlCsuStore(Lab.ConnA("Jelena")).DescribeDatabases(1);
            Assert.Equal(names.Count * 2, Convert.ToInt32(Lab.Scalar(Lab.ConnA("Jelena"), "SELECT COUNT(*) FROM dbo.JDBFiles")));
            var dbs = names.Select(n => new Vha.Core.Model.ManagedDatabase { Name = n }).ToList();
            using (var c = new Vha.Infrastructure.Sql.SqlNodeConnector(Vha.Core.Logging.NullLog.Instance).Connect(new Vha.Core.Model.NodeInfo { VmName = "VM1", ConnectionString = Lab.ConnA("Jelena") }))
                c.Node.ReleaseDatabases(dbs);
            Assert.Equal("0", Convert.ToString(Lab.Scalar(Lab.ConnA(), "SELECT COUNT(*) FROM sys.databases WHERE database_id > 4 AND name <> 'Jelena'")));
            Assert.True(Lab.Scalar(Lab.ConnA(), "SELECT 1 FROM sys.databases WHERE name = 'Jelena'") != null);   // never dropped by an injected statement
            // replicate the description to node B and attach there
            using (var s = new Vha.Infrastructure.Csu.SqlCsuStore(Lab.ConnB("Jelena")))
            {
                s.BeginTransaction(); s.CopyFromPeer(new Vha.Core.Model.NodeInfo { VmName = "VM1", ConnectionString = Lab.ConnA("Jelena") }, 1); s.Commit();
            }
            using (var cfg = new Vha.Infrastructure.Sql.SqlConfigStore(Lab.ConnB("Jelena"), ""))
                new Vha.Infrastructure.Sql.LocalSql(Lab.ConnB("Jelena"), "", 1, cfg, Vha.Core.Logging.NullLog.Instance).BringOnline(cfg.GetDatabases(1));
            foreach (var n in names)
                Assert.Equal("42", Convert.ToString(Lab.Scalar(Lab.ConnB(), "SELECT v FROM [" + n.Replace("]", "]]") + "].dbo.t")));
        }

        // ------------------------------------------------------------------ command line, ini, xml

        /// <summary>Reference implementation of the Microsoft C runtime argument parsing rules (CommandLineToArgvW for arguments after the program name).</summary>
        private static List<string> ParseArgs(string cmd)
        {
            var args = new List<string>(); var cur = new StringBuilder(); bool inQ = false, has = false; int i = 0;
            while (i < cmd.Length)
            {
                var c = cmd[i];
                if (c == '\\')
                {
                    int n = 0; while (i < cmd.Length && cmd[i] == '\\') { n++; i++; }
                    if (i < cmd.Length && cmd[i] == '"') { cur.Append('\\', n / 2); if (n % 2 == 1) { cur.Append('"'); i++; } has = true; }
                    else { cur.Append('\\', n); has = true; }
                    continue;
                }
                if (c == '"') { inQ = !inQ; has = true; i++; continue; }
                if ((c == ' ' || c == '\t') && !inQ) { if (has) { args.Add(cur.ToString()); cur.Clear(); has = false; } i++; continue; }
                cur.Append(c); has = true; i++;
            }
            if (has) args.Add(cur.ToString());
            return args;
        }

        [Fact]
        public void ProcessArgs_quote_round_trips_through_the_windows_argument_parser()
        {
            var r = new Random(9);
            for (int i = 0; i < 4000; i++)
            {
                var n = r.Next(1, 5);
                var args = Enumerable.Range(0, n).Select(_ => Random(r, 8, allowEmpty: true).Replace("\0", "")).ToList();
                var line = string.Join(" ", args.Select(ProcessArgs.Quote));
                var back = ParseArgs(line);
                Assert.True(args.SequenceEqual(back), "args " + string.Join(" | ", args.Select(Escape)) + " -> '" + line + "' -> " + string.Join(" | ", back.Select(Escape)));
            }
        }

        [Fact]
        public void Task_xml_arguments_survive_hostile_but_valid_names()
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var r = new Random(10);
            int checkedConfigs = 0;
            for (int i = 0; i < 1500; i++)
            {
                var c = Sample.Valid();
                c.Nodes[0].VmName = Random(r, 6, allowEmpty: false);
                c.AvailabilityGroups[0].Name = "ag" + Random(r, 3).Replace("\0", "");
                c.AvailabilityGroups[0].InitialOwner = c.Nodes[0].VmName;
                if (!DeploymentValidator.Validate(c).IsValid) continue;       // only configurations the validator accepts matter
                checkedConfigs++;
                var xml = TaskXmlRenderer.Render(c, 0, c.Nodes[0]);
                var doc = XDocument.Parse(xml);                               // always well formed
                var args = ParseArgs(doc.Descendants(ns + "Arguments").Single().Value);
                Assert.Equal(new[] { c.Nodes[0].VmName, c.AvailabilityGroups[0].Name, NodeNaming.SqlAddress(c.Nodes[0], c.AvailabilityGroups[0]), "2" }, args.ToArray());
            }
            Assert.True(checkedConfigs > 20, "too few valid random configurations: " + checkedConfigs);
        }

        [Fact]
        public void Ini_values_round_trip_and_random_text_never_breaks_the_parser()
        {
            var r = new Random(11);
            for (int i = 0; i < 3000; i++)
            {
                var ini = IniDocument.Parse(Random(r, 20));                    // arbitrary text must parse
                var key = "K" + r.Next(5); var value = Random(r, 6).Replace("\r", "").Replace("\n", "").Trim();
                ini.Set("Sec" + r.Next(3), key, value);
                var text = ini.ToString();
                var again = IniDocument.Parse(text);
                Assert.Equal(text, again.ToString());                          // stable
            }
            for (int i = 0; i < 1500; i++)
            {
                var ini = IniDocument.Empty();
                var sec = "S" + r.Next(4); var key = "k" + r.Next(6); var val = Random(r, 6).Replace("\r", "").Replace("\n", "").Trim();
                if (val.StartsWith(";") || val.StartsWith("#") || val.StartsWith("[")) val = "v" + val;   // would be a comment/section start only for values at line start; keys always have a prefix
                ini.Set(sec, key, val);
                Assert.Equal(val, IniDocument.Parse(ini.ToString()).Get(sec, key));
            }
        }

        // ------------------------------------------------------------------ parsers: only the documented exception types

        [Fact]
        public void Mutated_configuration_files_either_load_or_fail_with_FormatException_and_validation_never_throws()
        {
            var good = DeploymentConfigJson.Serialize(Sample.Valid());
            var r = new Random(12);
            int loaded = 0, rejected = 0;
            for (int i = 0; i < 6000; i++)
            {
                var sb = new StringBuilder(good);
                for (int m = r.Next(1, 4); m > 0; m--)
                {
                    var pos = r.Next(sb.Length);
                    switch (r.Next(4))
                    {
                        case 0: sb.Remove(pos, Math.Min(r.Next(1, 8), sb.Length - pos)); break;
                        case 1: sb.Insert(pos, Atoms[r.Next(Atoms.Length)]); break;
                        case 2: sb[pos] = "{}[]\",:0a".ToCharArray()[r.Next(9)]; break;
                        default: sb.Insert(pos, r.Next(100000).ToString()); break;
                    }
                }
                DeploymentConfig c;
                try { c = DeploymentConfigJson.Parse(sb.ToString()); loaded++; }
                catch (FormatException) { rejected++; continue; }
                var v = DeploymentValidator.Validate(c);                      // must not throw whatever the content
                if (v.IsValid)                                                // and everything derived from a valid config must be producible
                {
                    foreach (var n in c.Nodes) foreach (var s in SetupPlanBuilder.BuildNodePlan(c, n).Concat(SetupPlanBuilder.BuildClusterPlan(c, n))) { Assert.NotNull(s.Id); }
                    foreach (var ag in c.AvailabilityGroups) { IniRenderer.Render(c, ag, c.Nodes[0]); TaskXmlRenderer.Render(c, c.AvailabilityGroups.IndexOf(ag), c.Nodes[0]); }
                }
            }
            Assert.True(loaded > 100 && rejected > 100, loaded + " loaded, " + rejected + " rejected");
        }

        [Fact]
        public void The_vcenter_setting_parser_only_throws_FormatException_on_random_text()
        {
            var r = new Random(13);
            for (int i = 0; i < 6000; i++)
            {
                var text = r.Next(3) == 0 ? "Connect-VIServer " + Random(r, 10) : Random(r, 14);
                try { VCenterConnectionSpec.Parse(text, x => x); }
                catch (FormatException) { }
            }
        }

        [Fact]
        public void The_wizard_never_fails_with_an_unexpected_exception_on_random_answers()
        {
            var r = new Random(14);
            for (int i = 0; i < 400; i++)
            {
                var answers = Enumerable.Range(0, r.Next(0, 80)).Select(_ => r.Next(4) == 0 ? "" : r.Next(3) == 0 ? "s" : Random(r, 4)).ToList();
                var io = new ScriptedIO(answers);
                try { new Wizard(io).Run(); }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
            }
        }

        private sealed class ScriptedIO : IConsoleIO
        {
            private readonly Queue<string> _in; public ScriptedIO(IEnumerable<string> a) { _in = new Queue<string>(a); }
            public void WriteLine(string t) { }
            public string ReadLine() => _in.Count == 0 ? null : _in.Dequeue();
        }

        [Fact]
        public void Disk_discovery_and_matching_never_throw_unexpected_exceptions_on_random_disks()
        {
            var r = new Random(15);
            for (int i = 0; i < 3000; i++)
            {
                var disks = Enumerable.Range(0, r.Next(0, 6)).Select(_ => new Vha.Core.Model.VmDisk
                {
                    Id = r.Next(5) == 0 ? Random(r, 4) : "VirtualMachine-vm-" + r.Next(100) + "/" + (r.Next(3) == 0 ? r.Next(1990, 2070) : r.Next(1990, 2070)),
                    FileName = r.Next(6) == 0 ? "" : "[ds" + r.Next(3) + "] " + Random(r, 3),
                    Uuid = r.Next(3) == 0 ? "" : Random(r, 3), CapacityGB = r.NextDouble() * 100
                }).ToList();
                try { DiskIdentity.Join(new[] { new WindowsDisk { Number = 1, SerialNumber = Random(r, 3) } }, disks); } catch (InvalidOperationException) { }
                try { DiskDiscovery.Discover(r.Next(4) == 0 ? "*" : "*" + Random(r, 2).Replace("\0", "") + "*", new Dictionary<string, IReadOnlyList<Vha.Core.Model.VmDisk>> { { "A", disks } }, "A"); }
                catch (InvalidOperationException) { }
                try { DiskMatcher.Find(new Vha.Core.Model.ConfiguredDisk { DeviceKey = r.Next(1990, 2070).ToString(), FileName = "[ds0] x", UniqueId = Random(r, 2) }, disks); } catch (InvalidOperationException) { }
            }
        }

        private static string Escape(string s) => s == null ? "<null>" : "\"" + string.Concat(s.Select(c => c < 32 || c > 126 ? "\\u" + ((int)c).ToString("X4") : c.ToString())) + "\"";
    }
}
