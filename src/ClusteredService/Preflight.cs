using System;
using System.IO;
using System.Linq;
using Vha.Core.Config;
using Vha.Core.Logging;
using Vha.Core.Vmware;
using Vha.Infrastructure;
using Vha.Infrastructure.PowerShell;
using Vha.Infrastructure.Sql;

namespace ClusteredService
{
    /// <summary>
    /// ClusteredService.exe --check Jelena-&lt;AG&gt; : verifies the whole chain (ini, Jelena database, vCenter, disks, scheduled task)
    /// without moving anything. Run it after every installation or change and from monitoring.
    /// </summary>
    internal static class Preflight
    {
        private static int _failed;

        public static int Run(string serviceName)
        {
            ServiceSettings settings = null;
            Step("jelena.ini", () => { settings = ServiceSettings.FromIni(IniDocument.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jelena.ini")), serviceName); return settings.AgName + " on " + settings.VirtualMachineName; });
            if (settings == null) return 1;
            var log = new ConsoleLog();

            SqlConfigStore cfg = null;
            Step("Jelena database " + settings.Instance, () => { cfg = new SqlConfigStore(settings.Instance, settings.SqlOptions); return "connected"; });
            if (cfg == null) return 1;

            Vha.Core.Model.AgInfo ag = null; System.Collections.Generic.IReadOnlyList<Vha.Core.Model.NodeInfo> nodes = null;
            System.Collections.Generic.IReadOnlyList<Vha.Core.Model.ConfiguredDisk> disks = null;
            Step("JAGroups / JNodes / JDisks / JDatabases", () =>
            {
                ag = cfg.GetAg(settings.AgName) ?? throw new Exception("AG not in dbo.JAGroups");
                nodes = cfg.GetNodes(ag.Id); disks = cfg.GetDisks(ag.Id); var dbs = cfg.GetDatabases(ag.Id);
                if (nodes.Count < 2) throw new Exception("fewer than 2 nodes in dbo.JNodes");
                if (disks.Count == 0) throw new Exception("no disks in dbo.JDisks");
                if (dbs.Count == 0) throw new Exception("no databases in dbo.JDatabases");
                if (nodes.Count(n => n.VmName.Equals(settings.VirtualMachineName, StringComparison.OrdinalIgnoreCase)) != 1) throw new Exception("this VM is not exactly once in dbo.JNodes");
                return nodes.Count + " nodes, " + disks.Count + " disks, " + dbs.Count + " databases, owner " + ag.CurrentOwner;
            });
            Step("Scheduled task " + settings.TaskName, () => new TaskSchedulerCsuTask(settings.TaskName).Exists() ? "found" : throw new Exception("not found"));

            using (var ps = new PowerShellHost())
            using (var vc = new PowerCliVCenter(ps, log))
            {
                Step("vCenter connection", () => { vc.Connect(cfg.GetSetting("ConnectVCenter") ?? throw new Exception("JSettings ConnectVCenter missing")); return "connected"; });
                if (ag != null && disks != null && nodes != null)
                    foreach (var n in nodes)
                        Step("Disks on " + n.VmName, () =>
                        {
                            var vm = vc.GetVm(n.VmName); var vd = vc.GetDisks(n.VmName);
                            var held = disks.Where(d => DiskMatcher.Find(d, vd).Kind != MatchKind.None).Select(d => d.DeviceKey).ToList();
                            var noUuid = vd.Count(d => string.IsNullOrWhiteSpace(d.Uuid));
                            if (noUuid > 0) throw new Exception(noUuid + " disk(s) without UUID: set disk.EnableUUID=TRUE");
                            return vm.PowerState + ", snapshots " + vm.SnapshotCount + ", holds [" + string.Join(",", held) + "]";
                        });
                if (nodes != null)
                    foreach (var n in nodes.Where(x => !x.VmName.Equals(settings.VirtualMachineName, StringComparison.OrdinalIgnoreCase)))
                        Step("PowerShell remoting to " + n.DnsName, () => { ps.Invoke("Test-WSMan -ComputerName " + Vha.Core.Util.PsQuote.Single(n.DnsName) + " | Out-Null"); return "ok"; });
            }
            cfg.Dispose();
            Console.WriteLine(_failed == 0 ? "\r\nALL CHECKS PASSED" : "\r\n" + _failed + " CHECK(S) FAILED");
            return _failed == 0 ? 0 : 1;
        }

        private static void Step(string name, Func<string> check)
        {
            try { Console.WriteLine("[ OK ] " + name + ": " + check()); }
            catch (Exception ex) { _failed++; Console.WriteLine("[FAIL] " + name + ": " + ex.Message); }
        }

        private sealed class ConsoleLog : ILog
        {
            public void Debug(string m) { }
            public void Info(string m) { }
            public void Warn(string m) => Console.WriteLine("[WARN] " + m);
            public void Error(string m) => Console.WriteLine("[ERR ] " + m);
        }
    }
}
