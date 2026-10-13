using System;
using Vha.Core.Config;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Infrastructure;
using Vha.Infrastructure.PowerShell;
using Vha.Infrastructure.Sql;

namespace ClusteredService
{
    internal static class FailoverRunner
    {
        public static FailoverResult Run(ServiceSettings settings, ILog log)
        {
            using (var cfg = new SqlConfigStore(settings.Instance, settings.SqlOptions))
            using (var ps = new PowerShellHost())
            using (var vc = new PowerCliVCenter(ps, log))
            {
                var ag = cfg.GetAg(settings.AgName) ?? throw new InvalidOperationException("Availability group '" + settings.AgName + "' is not defined in dbo.JAGroups");
                var setting = cfg.GetSetting("ConnectVCenter");
                if (string.IsNullOrWhiteSpace(setting)) throw new InvalidOperationException("JSettings 'ConnectVCenter' is not defined");
                vc.Connect(setting);

                var coordinator = new FailoverCoordinator(settings, cfg, vc, new SqlNodeConnector(log), new GuestDisks(ps, log),
                    new LocalSql(settings.Instance, settings.SqlOptions, ag.Id, cfg, log), new TaskSchedulerCsuTask(settings.TaskName), new SystemClock(), log);
                return coordinator.Run();
            }
        }

        /// <summary>The VM holding the disks has snapshots: give the cluster group back to its node.</summary>
        public static void FailBack(ServiceSettings settings, string dnsName, ILog log)
        {
            try
            {
                using (var ps = new PowerShellHost(TimeSpan.FromMinutes(2)))
                {
                    var group = Vha.Core.Util.PsQuote.Single(settings.AgName);
                    try
                    {
                        ps.Invoke("Import-Module FailoverClusters; Move-ClusterGroup -Name " + group + " -Node " + Vha.Core.Util.PsQuote.Single(dnsName));
                        log.Info("Cluster group moved back to " + dnsName);
                    }
                    catch (Exception ex)
                    {
                        log.Error("Failback failed (" + ex.Message + "), stopping cluster group " + settings.AgName);
                        ps.Invoke("Import-Module FailoverClusters; Stop-ClusterGroup -Name " + group);
                    }
                }
            }
            catch (Exception ex) { log.Error("Failback procedure failed: " + ex.Message); }
        }
    }
}
