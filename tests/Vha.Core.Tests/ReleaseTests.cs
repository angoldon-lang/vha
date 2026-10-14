using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Vha;
using Xunit;

namespace Vha.Core.Tests
{
    public class ReleaseTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("Directory.Build.props not found");
        }

        private static string PropsVersion() => XDocument.Load(Path.Combine(Root(), "Directory.Build.props")).Descendants("VhaVersion").Single().Value.Trim();

        [Theory]
        [InlineData("2026.10.1", true)] [InlineData("2026.01.12", true)] [InlineData("2099.12.1", true)]
        [InlineData("2026.13.1", false)] [InlineData("2026.00.1", false)] [InlineData("2026.1.1", false)] [InlineData("2026.10.0", false)]
        [InlineData("3.0.0", false)] [InlineData("2026.10", false)] [InlineData("2026.10.1.0", false)] [InlineData("v2026.10.1", false)] [InlineData("", false)] [InlineData(null, false)]
        public void Calendar_version_format(string v, bool ok) => Assert.Equal(ok, VersionInfo.IsValid(v));

        [Fact]
        public void The_single_source_of_truth_is_a_valid_calendar_version() => Assert.True(VersionInfo.IsValid(PropsVersion()), PropsVersion());

        [Fact]
        public void The_assembly_carries_the_version_of_the_props_file() => Assert.Equal(PropsVersion(), VersionInfo.Current);

        [Fact]
        public void The_changelog_top_entry_matches_the_version_and_dates_are_valid()
        {
            var text = File.ReadAllText(Path.Combine(Root(), "CHANGELOG.md"));
            var m = Regex.Match(text, @"(?m)^## \[([^\]]+)\] - (\d{4})-(\d{2})-(\d{2})");
            Assert.True(m.Success);
            Assert.Equal(PropsVersion(), m.Groups[1].Value);
            var y = int.Parse(m.Groups[2].Value); var mo = int.Parse(m.Groups[3].Value);
            Assert.StartsWith(y + "." + mo.ToString("00") + ".", m.Groups[1].Value);   // year.month of the entry = date of the entry
            DateTime.ParseExact(m.Groups[2].Value + "-" + m.Groups[3].Value + "-" + m.Groups[4].Value, "yyyy-MM-dd", null);
        }

        [Fact]
        public void Changelog_versions_are_unique_and_valid()
        {
            var text = File.ReadAllText(Path.Combine(Root(), "CHANGELOG.md"));
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (Match m in Regex.Matches(text, @"(?m)^## \[([^\]]+)\]"))
            {
                Assert.True(VersionInfo.IsValid(m.Groups[1].Value), m.Groups[1].Value);
                Assert.True(seen.Add(m.Groups[1].Value), "duplicate " + m.Groups[1].Value);
            }
        }

        [Fact]
        public void No_project_pins_its_own_version()
        {
            foreach (var f in Directory.GetFiles(Path.Combine(Root(), "src"), "*.csproj", SearchOption.AllDirectories))
                Assert.DoesNotContain("<Version>", File.ReadAllText(f));
        }
    }
}
