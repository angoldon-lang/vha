using System.Collections;
using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;

namespace ClusteredService
{
    /// <summary>
    /// installutil /AGName=&lt;AG&gt; ClusteredService.exe  ->  service "Jelena-&lt;AG&gt;" (same as 2.0.0).
    /// The account is asked at install time (it must be a local administrator, see the installation guide).
    /// </summary>
    [RunInstaller(true)]
    public class ProjectInstaller : Installer
    {
        private readonly ServiceProcessInstaller _process = new ServiceProcessInstaller { Account = ServiceAccount.User };
        private readonly ServiceInstaller _service = new ServiceInstaller
        {
            ServiceName = "JCService",
            DisplayName = "Jelena clustered service",
            StartType = ServiceStartMode.Manual   // started by the cluster, never at boot
        };

        public ProjectInstaller() { Installers.AddRange(new Installer[] { _process, _service }); }

        protected override void OnBeforeInstall(IDictionary savedState) { SetServiceName(); base.OnBeforeInstall(savedState); }
        protected override void OnBeforeUninstall(IDictionary savedState) { SetServiceName(); base.OnBeforeUninstall(savedState); }

        private void SetServiceName()
        {
            if (Context.Parameters.ContainsKey("AGName"))
            {
                var ag = Context.Parameters["AGName"];
                _service.ServiceName = "Jelena-" + ag;
                _service.DisplayName = "Jelena clustered service for Availability Group " + ag;
            }
        }
    }
}
