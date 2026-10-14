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
            // After a power loss both nodes boot at once: the cluster may start this service while the local SQL Server is still starting.
            var attempts = Math.Max(1, Math.Min(24, (int)(settings.FailoverTimeout.TotalSeconds / 4 / 5)));
            using (var cfg = Vha.Core.Util.Retry.Run(() => new SqlConfigStore(settings.Instance, settings.SqlOptions), attempts, TimeSpan.FromSeconds(5), SqlNodeConnector.IsTransient,
                (n, ex) => log.Warn("The local SQL Server is not ready (attempt " + n + "/" + attempts + "): " + ex.Message)))
            using (var ps = new PowerShellHost())
            using (var vc = new PowerCliVCenter(ps, log))
            {
                var ag = cfg.GetAg(settings.AgName) ?? throw new InvalidOperationException("Availability group '" + settings.AgName + "' is not defined in dbo.JAGroups");
                var setting = cfg.GetSetting("ConnectVCenter");
                if (string.IsNullOrWhiteSpace(setting)) throw new InvalidOperationException("JSettings 'ConnectVCenter' is not defined");
                IVCenter vcenter = vc;
                try
                {
                    Vha.Core.Util.Retry.Run(() => { vc.Connect(setting); return 0; }, 3, TimeSpan.FromSeconds(10), ex => ex is PowerShellException,
                        (n, ex) => log.Warn("vCenter connection failed (attempt " + n + "/3): " + ex.Message));
                }
                catch (PowerShellException ex) when (settings.LocalStartWithoutVCenter)
                {
                    log.Error("vCenter is not reachable: " + ex.Message);
                    vcenter = new UnavailableVCenter(ex);
                }

                var coordinator = new FailoverCoordinator(settings, cfg, vcenter, new SqlNodeConnector(log, (int)settings.ReleaseTimeout.TotalSeconds), new GuestDisks(ps, log),
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
