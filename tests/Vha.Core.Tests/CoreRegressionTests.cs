using System;
using System.Collections;
using System.Linq;
using Vha.Core.Setup;
using Vha.Core.Sql;
using Vha.Core.Vmware;
using Xunit;

namespace Vha.Core.Tests
{
    public class RoundTwoRegressionTests
    {
        [Fact]
        public void Sql_literals_never_emit_a_raw_line_break_so_backslash_newline_cannot_be_swallowed()
        {
            // T-SQL removes "\" + line break inside a literal (line continuation)
            var lit = SqlText.Literal("C:\\dir\\\r\nnext");
            Assert.DoesNotContain("\r", lit); Assert.DoesNotContain("\n", lit);
            Assert.Equal("(N'C:\\dir\\' + NCHAR(13) + NCHAR(10) + N'next')", lit);
            Assert.Equal("(NCHAR(10))", SqlText.Literal("\n"));
            Assert.Equal("N'plain'", SqlText.Literal("plain"));
            Assert.Equal("(N'it''s' + NCHAR(13) + N'x')", SqlText.Literal("it's\rx"));
        }

        [Fact]
        public void File_literals_are_pure_literals_and_refuse_line_breaks()
        {
            Assert.Equal("N'M:\\a''b\\x.mdf'", SqlText.FileLiteral("M:\\a'b\\x.mdf"));
            Assert.Throws<ArgumentException>(() => SqlText.FileLiteral("a\nb"));
            Assert.Throws<ArgumentException>(() => AttachScriptBuilder.BuildAttach("x", new[] { new Vha.Core.Model.DatabaseFile { FileId = 1, PhysicalName = "a\r\nb" } }));
        }

        [Fact]
        public void Detach_and_offline_batches_take_the_name_from_a_variable()
        {
            var d = AttachScriptBuilder.BuildDetach("a\r\nb");
            Assert.StartsWith("DECLARE @n NVARCHAR(128) = (N'a' + NCHAR(13) + NCHAR(10) + N'b');", d);
            Assert.Contains("sp_detach_db @dbname = @n", d);
            Assert.Equal(1, d.Split(new[] { "DECLARE" }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("N'a\r", d); Assert.DoesNotContain("\nb'", d);
        }

        [Fact]
        public void Script_template_refuses_values_with_line_breaks()
        {
            Assert.Throws<ArgumentException>(() => SqlScriptTemplate.Apply("N'$(V)'", new System.Collections.Generic.Dictionary<string, string> { { "V", "a\\\nb" } }));
        }

        [Fact]
        public void Vcenter_parser_treats_typographic_quotes_like_powershell()
        {
            Assert.Equal("a\u2019b", VCenterConnectionSpec.Parse("Connect-VIServer vc -User u -Password \u2018a\u2019\u2019b\u2019").Password);
            Assert.Equal("x y", VCenterConnectionSpec.Parse("Connect-VIServer vc -User u -Password \u201Cx y\u201D").Password);
            Assert.Throws<FormatException>(() => VCenterConnectionSpec.Parse("Connect-VIServer vc -User u -Password \u2018unterminated"));
            Assert.Throws<FormatException>(() => VCenterConnectionSpec.Parse("Connect-VIServer vc -User u -Password ab\u2019cd"));   // unquoted quote character
        }

        public static System.Collections.Generic.IEnumerable<object[]> StringFields()
        {
            var c = Sample.Valid();
            void Walk(string prefix, object o)
            {
                foreach (var p in o.GetType().GetProperties().Where(x => x.CanWrite))
                {
                    var name = prefix + (prefix.Length == 0 ? "" : ".") + char.ToLowerInvariant(p.Name[0]) + p.Name.Substring(1);
                    var v = p.GetValue(o);
                    if (v is string) fields.Add(new object[] { name });
                    else if (v is IList l && l.Count > 0) for (int i = 0; i < l.Count; i++) Walk(name + "[" + i + "]", l[i]);
                    else if (v != null && p.PropertyType.Namespace == typeof(DeploymentConfig).Namespace && p.PropertyType.IsClass) Walk(name, v);
                }
            }
            fields = new System.Collections.Generic.List<object[]>();
            Walk("", c);
            return fields;
        }
        private static System.Collections.Generic.List<object[]> fields;

        [Theory]
        [MemberData(nameof(StringFields))]
        public void A_control_character_in_any_text_field_is_rejected(string path)
        {
            var c = Sample.Valid();
            SetByPath(c, path, "x\u0001y");
            var r = DeploymentValidator.Validate(c);
            Assert.Contains(r.Errors, e => e.Path == path && e.Message.Contains("control character"));
        }

        private static void SetByPath(object root, string path, string value)
        {
            object cur = root;
            var parts = path.Split('.');
            for (int k = 0; k < parts.Length; k++)
            {
                var part = parts[k]; int idx = -1;
                var br = part.IndexOf('[');
                if (br >= 0) { idx = int.Parse(part.Substring(br + 1, part.Length - br - 2)); part = part.Substring(0, br); }
                var prop = cur.GetType().GetProperties().First(p => string.Equals(p.Name, part, StringComparison.OrdinalIgnoreCase));
                if (k == parts.Length - 1) { prop.SetValue(cur, value); return; }
                var next = prop.GetValue(cur);
                cur = idx >= 0 ? ((IList)next)[idx] : next;
            }
        }
    }
}

namespace Vha.Core.Tests
{
    public class RetryTests
    {
        [Fact]
        public void Succeeds_after_transient_failures_waiting_between_attempts()
        {
            int calls = 0; var sleeps = new System.Collections.Generic.List<TimeSpan>(); var notified = 0;
            var r = Vha.Core.Util.Retry.Run(() => { if (++calls < 3) throw new TimeoutException("not yet"); return "ok"; }, 5, TimeSpan.FromSeconds(7), e => e is TimeoutException, (n, e) => notified++, sleeps.Add);
            Assert.Equal("ok", r); Assert.Equal(3, calls); Assert.Equal(2, notified);
            Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7) }, sleeps.ToArray());
        }

        [Fact]
        public void A_permanent_failure_is_not_retried()
        {
            int calls = 0;
            Assert.Throws<InvalidOperationException>(() => Vha.Core.Util.Retry.Run<int>(() => { calls++; throw new InvalidOperationException("login failed"); }, 5, TimeSpan.Zero, e => e is TimeoutException, null, t => { }));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Gives_up_with_the_last_transient_error_after_the_attempts()
        {
            int calls = 0;
            var ex = Assert.Throws<TimeoutException>(() => Vha.Core.Util.Retry.Run<int>(() => { calls++; throw new TimeoutException("attempt " + calls); }, 4, TimeSpan.Zero, e => true, null, t => { }));
            Assert.Equal("attempt 4", ex.Message); Assert.Equal(4, calls);
            Assert.Throws<ArgumentOutOfRangeException>(() => Vha.Core.Util.Retry.Run(() => 1, 0, TimeSpan.Zero, e => true));
        }
    }
}
