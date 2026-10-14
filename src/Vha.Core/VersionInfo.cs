using System;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Vha
{
    /// <summary>Release number (calendar versioning YYYY.MM.N) stamped into every assembly from Directory.Build.props.</summary>
    public static class VersionInfo
    {
        public static readonly Regex CalVer = new Regex(@"^(20\d\d)\.(0[1-9]|1[0-2])\.([1-9]\d*)$", RegexOptions.Compiled);

        /// <summary>E.g. "2026.10.1".</summary>
        public static string Current
        {
            get
            {
                var a = typeof(VersionInfo).Assembly;
                var info = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
                var plus = info.IndexOf('+');
                if (plus >= 0) info = info.Substring(0, plus);
                return info.Length > 0 ? info : "0.0.0";
            }
        }

        public static string Banner(string product) => product + " " + Current;

        public static bool IsValid(string version) => version != null && CalVer.IsMatch(version);
    }
}
