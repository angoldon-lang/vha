using System;
using System.Collections.Generic;
using System.Linq;
using Vha.Core.Failover;
using Vha.Core.Logging;
using Vha.Core.Setup;
using Vha.Infrastructure.PowerShell;

namespace Vha.Infrastructure.Setup
{
    public sealed class RunnerOptions
    {
        public string PackageDir { get; set; }
        public string PrereqDir { get; set; }
        public bool DryRun { get; set; }
        public bool ShowScripts { get; set; }
    }

    /// <summary>Executes a plan step by step, stopping at the first failure or when a restart is needed. Re-running is always safe.</summary>
    public sealed class SetupRunner
    {
        private readonly DeploymentConfig _c;
        private readonly NodeSettings _node;
        private readonly RunnerOptions _o;
        private readonly ILog _log;
        private readonly ISecretProvider _secrets;
        private readonly Func<PowerShellHost> _hostFactory;
        private readonly DatabaseSetup _db;
        private readonly Func<PowerShellHost, Vha.Core.Failover.IVCenter> _vcenterFactory;
        private readonly Func<string, string, string> _rights;
        private PowerShellHost _host;

        public SetupRunner(DeploymentConfig c, NodeSettings node, RunnerOptions o, ILog log, ISecretProvider secrets, DatabaseSetup db,
            Func<PowerShellHost> hostFactory = null, Func<PowerShellHost, Vha.Core.Failover.IVCenter> vcenterFactory = null, Func<string, string, string> rights = null)
        {
            _c = c; _node = node; _o = o; _log = log; _secrets = secrets; _db = db;
            _hostFactory = hostFactory ?? (() => new PowerShellHost(TimeSpan.FromMinutes(30)));
            _vcenterFactory = vcenterFactory;
            _rights = rights ?? ((account, right) => RightsGrant.Ensure(account, right, log));
        }

        public RunReport Run(IReadOnlyList<SetupStep> steps)
        {
            var report = new RunReport();
            int n = 0;
            try
            {
                foreach (var step in steps)
                {
                    n++;
                    _log.Info("[" + n + "/" + steps.Count + "] " + step.Title);
                    var result = new StepResult { StepId = step.Id };
                    report.Results.Add(result);
                    if (_o.DryRun)
                    {
                        result.Outcome = StepOutcome.Skipped;
                        if (_o.ShowScripts && step.Script != null) foreach (var l in step.Script.Replace("\r\n", "\n").Split('\n')) _log.Info("      | " + l);
                        continue;
                    }
                    try
                    {
                        foreach (var line in Execute(step)) { result.Output.Add(line); _log.Info("      " + line); }
                        result.Outcome = result.Output.Any(l => l.Contains(SetupPlanBuilder.RebootMarker)) ? StepOutcome.RebootRequired : StepOutcome.Ok;
                    }
                    catch (Exception ex)
                    {
                        result.Outcome = StepOutcome.Failed;
                        result.Error = ex.Message;
                        _log.Error("      FAILED: " + ex.Message);
                        break;
                    }
                    if (result.Outcome == StepOutcome.RebootRequired) { _log.Warn("A restart is required before the setup can continue: restart the server and run the same command again."); break; }
                }
            }
            finally { _host?.Dispose(); _host = null; }
            return report;
        }

        private IEnumerable<string> Execute(SetupStep step)
        {
            if (step.Script != null)
            {
                _host = _host ?? _hostFactory();
                var vars = new Dictionary<string, object>();
                foreach (var v in step.Variables) vars[v] = Variable(v);
                return _host.Invoke(step.Script, vars).Select(o => o?.ToString() ?? "").Where(s => s.Length > 0).ToList();
            }
            if (step.Id == "rights")
                return new[]
                {
                    _rights(_c.ServiceAccount, RightsGrant.ServiceLogon),
                    _rights(_c.TaskAccount, RightsGrant.BatchLogon)
                };
            if (step.Id.StartsWith("database:", StringComparison.Ordinal))
            {
                var ag = _c.AvailabilityGroups.First(a => a.Name == step.AgName);
                _host = _host ?? _hostFactory();
                var password = string.IsNullOrWhiteSpace(_c.VCenter.User) ? null : _secrets.VCenterPassword();
                var setting = _db.ConnectVCenterSetting(_c, password);
                IVCenter vc;
                PowerCliVCenter real = null;
                if (_vcenterFactory != null) vc = _vcenterFactory(_host);
                else { real = new PowerCliVCenter(_host, _log); real.Connect(setting); vc = real; }
                try { return _db.Run(_c, ag, _node, password, vc); }
                finally { real?.Dispose(); }
            }
            throw new InvalidOperationException("Unknown built-in step " + step.Id);
        }

        private object Variable(string name)
        {
            switch (name)
            {
                case "packageDir": return _o.PackageDir;
                case "prereqDir": return _o.PrereqDir;
                case "svcPassword": return _secrets.ServiceAccountPassword();
                case "taskPassword": return _secrets.TaskAccountPassword();
                default: throw new InvalidOperationException("Unknown step variable " + name);
            }
        }
    }
}
