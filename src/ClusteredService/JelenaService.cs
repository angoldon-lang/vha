using System;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Vha.Core.Config;
using Vha.Core.Failover;
using Vha.Core.Logging;

namespace ClusteredService
{
    public sealed class JelenaService : ServiceBase
    {
        public JelenaService() { ServiceName = "Jelena"; CanStop = true; AutoLog = true; }

        protected override void OnStart(string[] args)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var name = ServiceNameResolver.Resolve();
            ILog log = NullLog.Instance;
            try
            {
                var settings = ServiceSettings.FromIni(IniDocument.Load(Path.Combine(baseDir, "jelena.ini")), name);
                log = new FileLog(Path.Combine(baseDir, name + "-Log"), settings.LogRetentionDays);
                log.Info("************* STARTING OPERATIONS (service " + name + ", vHA " + Vha.VersionInfo.Current + ") *************");

                // The SCM gives a service 30 seconds to start; a failover can take minutes. Keep START_PENDING alive.
                using (new Timer(_ => { try { RequestAdditionalTime(60000); } catch (Exception) { } }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)))
                {
                    var job = Task.Run(() => FailoverRunner.Run(settings, log));
                    if (!job.Wait(settings.FailoverTimeout))
                        throw new System.TimeoutException("Failover did not complete within " + settings.FailoverTimeout.TotalSeconds + " seconds");
                    var result = job.Result;
                    log.Info("Failover outcome: " + result.Outcome);
                    if (result.Outcome == FailoverOutcome.FailbackRequired)
                        Task.Run(() => { Thread.Sleep(2000); FailoverRunner.FailBack(settings, result.FailbackNode.DnsName, log); });
                }
            }
            catch (AggregateException ex)
            {
                var inner = ex.GetBaseException();
                log.Error("Failover failed: " + inner);
                throw new InvalidOperationException(inner.Message, inner);   // the resource fails and the cluster reacts
            }
            catch (Exception ex)
            {
                log.Error("Service start failed: " + ex);
                throw;
            }
        }

        protected override void OnStop() { }
    }

    internal static class ServiceNameResolver
    {
        /// <summary>The installed service name (Jelena-&lt;AG&gt;): found through the Win32_Service row of this process.</summary>
        public static string Resolve()
        {
            using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Service WHERE ProcessId = " + System.Diagnostics.Process.GetCurrentProcess().Id))
                foreach (ManagementObject o in s.Get())
                    return Convert.ToString(o["Name"]);
            throw new InvalidOperationException("Cannot determine the service name of this process");
        }
    }
}
