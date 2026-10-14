using System.Collections.Generic;
using System.Linq;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Model;
using Vha.Core.Vmware;

namespace Vha.Infrastructure.PowerShell
{
    public sealed class GuestDisks : IGuestDisks
    {
        private readonly PowerShellHost _ps; private readonly ILog _log;
        public GuestDisks(PowerShellHost ps, ILog log) { _ps = ps; _log = log; }

        public void SetOffline(NodeInfo node, IReadOnlyList<ConfiguredDisk> disks)
        {
            if (disks.Count == 0) return;
            _log.Info("Setting " + disks.Count + " disk(s) offline on " + node.DnsName);
            _ps.Invoke(PowerCliScripts.SetDiskOffline(disks, node.DnsName));
        }

        public IReadOnlyList<ConfiguredDisk> MissingLocally(IReadOnlyList<ConfiguredDisk> disks)
        {
            if (disks.Count == 0) return disks;
            var output = _ps.Invoke(PowerCliScripts.MissingLocalDisks(disks));
            var missing = new HashSet<string>(output.Select(o => (o?.ToString() ?? "").Trim()).Where(o => o.StartsWith("MISSING:")).Select(o => o.Substring(8)), System.StringComparer.Ordinal);
            return disks.Where(d => missing.Contains(d.UniqueId)).ToList();
        }

        public void SetOnlineLocal(IReadOnlyList<ConfiguredDisk> disks)
        {
            if (disks.Count == 0) return;
            _log.Info("Setting " + disks.Count + " disk(s) online");
            _ps.Invoke(PowerCliScripts.SetDiskOnlineLocal(disks));
        }
    }
}
