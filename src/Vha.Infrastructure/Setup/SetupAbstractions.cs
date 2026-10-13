using System;
using System.Collections.Generic;
using Vha.Core.Logging;

namespace Vha.Infrastructure.Setup
{
    /// <summary>The passwords the setup needs. Never stored: asked at run time (or taken from environment variables) and passed on as host variables.</summary>
    public interface ISecretProvider
    {
        string ServiceAccountPassword();
        string TaskAccountPassword();
        string VCenterPassword();
    }

    public interface IPasswordProtector
    {
        /// <summary>Returns "dpapi:&lt;base64&gt;".</summary>
        string Protect(string plain);
    }

    public sealed class DpapiProtector : IPasswordProtector
    {
        public string Protect(string plain) => SecretProtector.Protect(plain);
    }

    public sealed class EnvironmentOrPromptSecrets : ISecretProvider
    {
        private readonly Func<string, string> _prompt;
        public EnvironmentOrPromptSecrets(Func<string, string> prompt) { _prompt = prompt; }
        public string ServiceAccountPassword() => Env("VHA_SERVICE_PASSWORD") ?? _prompt("Password dell'account del servizio");
        public string TaskAccountPassword() => Env("VHA_TASK_PASSWORD") ?? _prompt("Password dell'account del task schedulato");
        public string VCenterPassword() => Env("VHA_VCENTER_PASSWORD") ?? _prompt("Password dell'utente vCenter");
        private static string Env(string n) { var v = Environment.GetEnvironmentVariable(n); return string.IsNullOrEmpty(v) ? null : v; }
    }

    public enum StepOutcome { Ok, Failed, RebootRequired, Skipped }

    public sealed class StepResult
    {
        public string StepId { get; set; }
        public StepOutcome Outcome { get; set; }
        public List<string> Output { get; } = new List<string>();
        public string Error { get; set; }
    }

    public sealed class RunReport
    {
        public List<StepResult> Results { get; } = new List<StepResult>();
        public bool Success => Results.TrueForAll(r => r.Outcome == StepOutcome.Ok || r.Outcome == StepOutcome.Skipped);
        public bool RebootRequired => Results.Exists(r => r.Outcome == StepOutcome.RebootRequired);
    }
}
