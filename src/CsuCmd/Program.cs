using System;
using System.IO;
using System.Threading;
using Vha.Core.Csu;
using Vha.Core.Logging;
using Vha.Infrastructure;
using Vha.Infrastructure.Csu;
using Vha.Infrastructure.PowerShell;
using Vha.Infrastructure.Sql;

namespace CsuCmd
{
    internal static class Program
    {
        private const string Usage =
            "CsuCmd.exe <VirtualMachineName> <AvailabilityGroupName> <Instance> <LogLevel>\r\n" +
            "  LogLevel: 1 = errors and warnings, 2 = info, 3 = debug\r\n" +
            "CsuCmd.exe --protect     reads a password from stdin and prints the dpapi:... value for JSettings.ConnectVCenter";

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--protect")
            {
                Console.Error.WriteLine("Password (stdin):");
                var line = Console.ReadLine();
                if (string.IsNullOrEmpty(line)) return 2;
                Console.WriteLine(SecretProtector.Protect(line));
                return 0;
            }

            if (args.Length != 4 || !int.TryParse(args[3], out var level) || level < 1 || level > 3)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
            string vm = args[0].Trim(), ag = args[1].Trim(), instance = args[2].Trim();

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var log = new CompositeLog(
                new LevelLog(new WindowsEventLog("Jelena Console Update - " + ag), level),
                new LevelLog(new FileLog(Path.Combine(baseDir, "Csu-" + ag + "-Log")), level));

            // The scheduled task can fire again while a slow run is still active: never run two at once.
            using (var mutex = new Mutex(false, @"Global\VhaCsu-" + Sanitize(ag)))
            {
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { log.Warn("Previous CSU run for " + ag + " is still active, skipping this run"); return 0; }
                try
                {
                    log.Info("CSU started: VM=" + vm + " AG=" + ag + " instance=" + instance);
                    using (var ps = new PowerShellHost())
                    using (var cfg = new SqlConfigStore(instance, ""))
                    using (var vc = new PowerCliVCenter(ps, log))
                    using (var store = new SqlCsuStore(instance))
                    {
                        var setting = cfg.GetSetting("ConnectVCenter");
                        if (string.IsNullOrWhiteSpace(setting)) throw new InvalidOperationException("JSettings 'ConnectVCenter' is not defined");
                        vc.Connect(setting);
                        var csu = new CsuCoordinator(vm, ag, store, new SqlPeerProbe(), new WindowsDiskInventory(ps, vc, vm, log), new SqlLocalDatabases(instance), log);
                        var r = csu.Run();
                        log.Info("CSU finished: synced=" + r.Synced + " disksChanged=" + r.DisksChanged + " unreachablePeers=" + r.UnreachablePeers + " dropped=" + r.Dropped.Count);
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    log.Error("CSU failed: " + ex);
                    return 1;
                }
                finally { try { mutex.ReleaseMutex(); } catch (Exception) { } }
            }
        }

        private static string Sanitize(string s) => s.Replace('\\', '_').Replace('/', '_');
    }
}
