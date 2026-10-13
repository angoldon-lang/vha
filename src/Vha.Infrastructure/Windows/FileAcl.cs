using System;
using System.Diagnostics;
using Vha.Core.Logging;
using Vha.Core.Util;

namespace Vha.Infrastructure
{
    /// <summary>
    /// Gives the database files back the ACL inherited from the mount point folder (the service account may own them
    /// after a move). The original called "icacls &lt;file&gt;\* /q /c /t" which only lists ACLs, never quoted paths,
    /// and ignored every exit code.
    /// </summary>
    public static class FileAcl
    {
        public static void Restore(string path, ILog log)
        {
            Run("takeown.exe", "/f " + ProcessArgs.Quote(path), path, log);
            Run("icacls.exe", ProcessArgs.Quote(path) + " /reset /c /q", path, log);
        }

        private static void Run(string exe, string args, string path, ILog log)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch (Exception) { } log.Warn(exe + " timed out for " + path); return; }
                    if (p.ExitCode != 0) log.Warn(exe + " " + args + " exited with " + p.ExitCode + ": " + err.Result.Trim() + " " + output.Result.Trim());
                    else log.Debug(exe + " ok for " + path);
                }
            }
            catch (Exception ex)
            {
                log.Warn(exe + " could not be started for " + path + ": " + ex.Message);
            }
        }
    }
}
