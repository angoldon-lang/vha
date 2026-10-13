using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Vha.Core.Logging;
using Vha.Core.Setup;

namespace Vha.Infrastructure.Setup
{
    /// <summary>
    /// Grants "Log on as a service" / "Log on as a batch job" with secedit. The INF text editing is unit tested
    /// (<see cref="UserRightsInf"/>); the secedit calls need Windows.
    /// </summary>
    public static class RightsGrant
    {
        public const string ServiceLogon = "SeServiceLogonRight";
        public const string BatchLogon = "SeBatchLogonRight";

        public static string Ensure(string account, string right, ILog log)
        {
            string sid;
            try { sid = new NTAccount(account.Trim()).Translate(typeof(SecurityIdentifier)).Value; }
            catch (Exception ex) { throw new InvalidOperationException("The account '" + account + "' cannot be resolved: " + ex.Message, ex); }

            var tmp = Path.Combine(Path.GetTempPath(), "vha-sec-" + Guid.NewGuid().ToString("N"));
            var cfg = tmp + ".inf"; var db = tmp + ".sdb";
            try
            {
                Run("secedit.exe", "/export /cfg \"" + cfg + "\" /areas USER_RIGHTS", log);
                var inf = File.ReadAllText(cfg, Encoding.Unicode);
                if (UserRightsInf.Contains(inf, right, sid)) return right + " already granted to " + account;
                File.WriteAllText(cfg, UserRightsInf.AddAccount(inf, right, sid), Encoding.Unicode);
                Run("secedit.exe", "/configure /db \"" + db + "\" /cfg \"" + cfg + "\" /areas USER_RIGHTS", log);

                Run("secedit.exe", "/export /cfg \"" + cfg + "\" /areas USER_RIGHTS", log);
                if (!UserRightsInf.Contains(File.ReadAllText(cfg, Encoding.Unicode), right, sid))
                    throw new InvalidOperationException(right + " was not applied to " + account + " (a domain policy may override it: add the right in the GPO)");
                return right + " granted to " + account;
            }
            finally
            {
                foreach (var f in new[] { cfg, db, tmp + ".jfm", tmp + ".log" }) { try { if (File.Exists(f)) File.Delete(f); } catch (Exception) { } }
            }
        }

        private static void Run(string exe, string args, ILog log)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch (Exception) { } throw new InvalidOperationException(exe + " timed out"); }
                if (p.ExitCode != 0) throw new InvalidOperationException(exe + " " + args + " failed (" + p.ExitCode + "): " + o.Result.Trim() + " " + e.Result.Trim());
                log.Debug(exe + " ok");
            }
        }
    }
}
