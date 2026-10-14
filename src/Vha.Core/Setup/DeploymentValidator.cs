using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Vha.Core.Setup
{
    public enum Severity { Error, Warning }

    public sealed class ValidationIssue
    {
        public Severity Severity { get; set; }
        public string Path { get; set; }
        public string Message { get; set; }
        public override string ToString() => (Severity == Severity.Error ? "ERROR   " : "WARNING ") + Path + ": " + Message;
    }

    public sealed class ValidationResult
    {
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();
        public bool IsValid => Issues.All(i => i.Severity != Severity.Error);
        public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == Severity.Error);
        public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == Severity.Warning);
        internal void Error(string path, string msg) => Issues.Add(new ValidationIssue { Severity = Severity.Error, Path = path, Message = msg });
        internal void Warn(string path, string msg) => Issues.Add(new ValidationIssue { Severity = Severity.Warning, Path = path, Message = msg });
    }

    /// <summary>All the rules a configuration must satisfy before anything is installed. Every rule has a test.</summary>
    public static class DeploymentValidator
    {
        private static readonly Regex AgNameRx = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
        private static readonly Regex InstanceRx = new Regex(@"^[A-Za-z_][A-Za-z0-9_$#@-]{0,15}$", RegexOptions.Compiled);
        private static readonly Regex HostLabelRx = new Regex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$", RegexOptions.Compiled);
        private static readonly Regex VmNameRx = new Regex(@"^[^\\/:*?""<>|'`$;&(){}\r\n\t]{1,80}$", RegexOptions.Compiled);
        private static readonly Regex AbsWinPathRx = new Regex(@"^[A-Za-z]:\\([^<>:""/|?*\r\n]+\\?)*$", RegexOptions.Compiled);
        private static readonly Regex DriveRootRx = new Regex(@"^[A-Za-z]:\\?$", RegexOptions.Compiled);
        private static readonly Regex UncRx = new Regex(@"^\\\\[A-Za-z0-9._-]+\\[^<>:""/|?*\r\n\\]+(\\[^<>:""/|?*\r\n\\]+)*$", RegexOptions.Compiled);
        private static readonly Regex AccountRx = new Regex(@"^([A-Za-z0-9._-]{1,63}\\[A-Za-z0-9._$-]{1,64}|[A-Za-z0-9._-]{1,64}@[A-Za-z0-9.-]{1,253})$", RegexOptions.Compiled);
        private static readonly Regex ClusterNameRx = new Regex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,14}$", RegexOptions.Compiled);
        private static readonly Regex OptionsRx = new Regex(@"^[A-Za-z0-9 =;._,:\\/-]*$", RegexOptions.Compiled);
        private static readonly Regex TaskNameRx = new Regex(@"^[A-Za-z0-9 ._-]{1,100}$", RegexOptions.Compiled);
        private static readonly Regex FilterRx = new Regex(@"^[^\r\n'`$;|&]{1,200}$", RegexOptions.Compiled);

        public static ValidationResult Validate(DeploymentConfig c)
        {
            var r = new ValidationResult();
            if (c == null) { r.Error("config", "missing"); return r; }
            ControlCharacters(r, "", c);
            if (c.SchemaVersion != 1) r.Error("schemaVersion", "unsupported version " + c.SchemaVersion + " (expected 1)");

            Path(r, "installDirectory", c.InstallDirectory, allowRoot: false);

            // ---- vCenter
            if (c.VCenter == null) r.Error("vCenter", "missing");
            else
            {
                if (!IsHostOrIp(c.VCenter.Server)) r.Error("vCenter.server", "must be a host name, FQDN or IP address");
                if (!string.IsNullOrWhiteSpace(c.VCenter.User) && !AccountRx.IsMatch(c.VCenter.User)) r.Error("vCenter.user", "use DOMAIN\\user or user@domain");
                if (c.VCenter.AllowUntrustedCertificate) r.Warn("vCenter.allowUntrustedCertificate", "vCenter certificates will not be validated: install the CA certificate on the nodes instead");
            }

            // ---- accounts
            Account(r, "serviceAccount", c.ServiceAccount);
            Account(r, "taskAccount", c.TaskAccount);

            // ---- nodes
            var nodes = c.Nodes ?? new List<NodeSettings>();
            if (nodes.Count < 2) r.Error("nodes", "at least two nodes are required (found " + nodes.Count + ")");
            if (nodes.Count > 8) r.Error("nodes", "more than 8 nodes is not supported");
            for (int i = 0; i < nodes.Count; i++)
            {
                var p = "nodes[" + i + "]";
                if (nodes[i] == null) { r.Error(p, "missing"); continue; }
                if (string.IsNullOrWhiteSpace(nodes[i].VmName) || !VmNameRx.IsMatch(nodes[i].VmName)) r.Error(p + ".vmName", "invalid virtual machine name (no quotes, slashes, ';', '&', '$', backticks or control characters)");
                if (!IsFqdnOrShort(nodes[i].DnsName)) r.Error(p + ".dnsName", "must be a DNS host name");
            }
            Duplicates(r, "nodes", nodes.Where(n => n != null).Select(n => n.VmName), "vmName");
            Duplicates(r, "nodes", nodes.Where(n => n != null).Select(n => n.DnsName), "dnsName");

            // ---- cluster
            if (c.Cluster == null) r.Error("cluster", "missing");
            else
            {
                if (string.IsNullOrWhiteSpace(c.Cluster.Name) || !ClusterNameRx.IsMatch(c.Cluster.Name)) r.Error("cluster.name", "NetBIOS name: letters, digits and '-', up to 15 characters");
                if (!IPAddress.TryParse(c.Cluster.IpAddress ?? "", out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || (c.Cluster.IpAddress ?? "").Split('.').Length != 4)
                    r.Error("cluster.ipAddress", "must be an IPv4 address");
                if (c.Cluster.Witness == WitnessType.FileShare)
                {
                    if (!UncRx.IsMatch(c.Cluster.WitnessPath ?? "")) r.Error("cluster.witnessPath", "must be a UNC path like \\\\server\\share\\folder");
                }
                else if (nodes.Count % 2 == 0 && nodes.Count > 0)
                    r.Error("cluster.witness", "an even number of nodes needs a witness (quorum): choose FileShare");
                if (c.Cluster.FailoverThreshold < 0 || c.Cluster.FailoverThreshold > 100) r.Error("cluster.failoverThreshold", "0..100");
                if (c.Cluster.FailoverPeriodHours < 0 || c.Cluster.FailoverPeriodHours > 24 * 7) r.Error("cluster.failoverPeriodHours", "0..168");
                if ((c.Cluster.FailoverThreshold > 0) != (c.Cluster.FailoverPeriodHours > 0)) r.Error("cluster.failoverThreshold", "failoverThreshold and failoverPeriodHours must be set together");
            }

            // ---- availability groups
            var ags = c.AvailabilityGroups ?? new List<AgSettings>();
            if (ags.Count == 0) r.Error("availabilityGroups", "at least one availability group is required");
            Duplicates(r, "availabilityGroups", ags.Where(a => a != null).Select(a => a.Name), "name");
            Duplicates(r, "availabilityGroups", ags.Where(a => a != null).Select(a => a.MountRoot), "mountRoot");
            Duplicates(r, "availabilityGroups", ags.Where(a => a != null).Select(a => a.ListenerName), "listenerName");
            Duplicates(r, "availabilityGroups", ags.Where(a => a != null).Select(a => a.ListenerIp).Concat(new[] { c.Cluster?.IpAddress }), "IP address");
            Duplicates(r, "availabilityGroups", ags.Where(a => a != null).Select(a => a.EffectiveTaskName), "task name");
            for (int i = 0; i < ags.Count; i++)
            {
                var a = ags[i]; var p = "availabilityGroups[" + i + "]";
                if (a == null) { r.Error(p, "missing"); continue; }
                if (!AgNameRx.IsMatch(a.Name ?? "")) r.Error(p + ".name", "letters, digits, '.', '_' and '-' (max 64), starting with a letter or digit");
                if (!string.IsNullOrEmpty(a.InstanceName) && !InstanceRx.IsMatch(a.InstanceName)) r.Error(p + ".instanceName", "invalid SQL Server instance name");
                if (a.SqlPort < 0 || a.SqlPort > 65535) r.Error(p + ".sqlPort", "0..65535");
                Path(r, p + ".mountRoot", a.MountRoot, allowRoot: false);
                if (!string.IsNullOrWhiteSpace(a.MountRoot) && DriveRootRx.IsMatch(a.MountRoot)) r.Error(p + ".mountRoot", "use a folder below the mount point disk, not a drive root");
                if (!FilterRx.IsMatch(a.DiskNameFilter ?? "")) r.Error(p + ".diskNameFilter", "required; wildcard on the VMDK file name; no quotes, ';', '|', '&', '$' or backticks");
                else if ((a.DiskNameFilter ?? "").Trim() == "*") r.Warn(p + ".diskNameFilter", "'*' selects every disk of the VM: the operating system disk would be included");
                if (!nodes.Any(n => n != null && string.Equals(n.VmName, a.InitialOwner, StringComparison.OrdinalIgnoreCase))) r.Error(p + ".initialOwner", "must be the vmName of one of the nodes");
                if (a.ReservationEnabled)
                {
                    if (a.ReservationCpuMHz < 0 || a.ReservationCpuMHz > 1000000) r.Error(p + ".reservationCpuMHz", "0..1000000");
                    if (a.ReservationMemMB < 0 || a.ReservationMemMB > 100000000) r.Error(p + ".reservationMemMB", "0..100000000");
                    if (a.ReservationCpuMHz == 0 && a.ReservationMemMB == 0) r.Warn(p, "reservations are enabled but both values are 0");
                }
                if (string.IsNullOrWhiteSpace(a.ListenerName) || !ClusterNameRx.IsMatch(a.ListenerName)) r.Error(p + ".listenerName", "NetBIOS name: letters, digits and '-', up to 15 characters");
                if (!IPAddress.TryParse(a.ListenerIp ?? "", out var lip) || lip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || (a.ListenerIp ?? "").Split('.').Length != 4) r.Error(p + ".listenerIp", "must be an IPv4 address");
                if (!Enum.IsDefined(typeof(OwnerPolicy), a.OwnerUnreachablePolicy)) r.Error(p + ".ownerUnreachablePolicy", "Force or Abort");
                if (a.CsuWaitSeconds < 1 || a.CsuWaitSeconds > 3600) r.Error(p + ".csuWaitSeconds", "1..3600");
                if (a.FailoverTimeoutSeconds < 30 || a.FailoverTimeoutSeconds > 7200) r.Error(p + ".failoverTimeoutSeconds", "30..7200");
                if (a.ReleaseTimeoutSeconds < 10 || a.ReleaseTimeoutSeconds > 3600) r.Error(p + ".releaseTimeoutSeconds", "10..3600");
                else if (a.ReleaseTimeoutSeconds >= a.FailoverTimeoutSeconds) r.Warn(p + ".releaseTimeoutSeconds", "not shorter than failoverTimeoutSeconds: a stuck previous owner would use the whole failover time");
                if (a.CsuLogLevel < 1 || a.CsuLogLevel > 3) r.Error(p + ".csuLogLevel", "1, 2 or 3");
                if (a.CsuIntervalSeconds < 60 || a.CsuIntervalSeconds > 3600 || a.CsuIntervalSeconds % 60 != 0) r.Error(p + ".csuIntervalSeconds", "a multiple of 60 between 60 and 3600 (Task Scheduler repeats at whole minutes; the guide recommends 60)");
                if (!OptionsRx.IsMatch(a.SqlOptions ?? "")) r.Error(p + ".sqlOptions", "only letters, digits and = ; . _ , : \\ / - are allowed");
                else if ((a.SqlOptions ?? "").IndexOf("TrustServerCertificate=True", StringComparison.OrdinalIgnoreCase) >= 0) r.Warn(p + ".sqlOptions", "TrustServerCertificate=True skips certificate validation: install a trusted certificate on SQL Server when possible");
                if (a.CreateDummyDatabase && !Regex.IsMatch(a.DummyDatabaseName ?? "", @"^[A-Za-z_][A-Za-z0-9_]{0,63}$")) r.Error(p + ".dummyDatabaseName", "letters, digits and '_' only");
                if (!string.IsNullOrWhiteSpace(a.TaskName) && !TaskNameRx.IsMatch(a.TaskName)) r.Error(p + ".taskName", "letters, digits, space, '.', '_' and '-'");
                if (a.EffectiveTaskName.Length > 100) r.Error(p + ".taskName", "task name longer than 100 characters");
                if (("Jelena-" + a.Name).Length > 200) r.Error(p + ".name", "service name too long");
            }
            // two AGs on the same instance/drive are legal, two AGs with the same name on different instances are not
            return r;
        }

        /// <summary>No text field may contain a control character (they break XML, INI and command lines).</summary>
        private static void ControlCharacters(ValidationResult r, string prefix, object o)
        {
            if (o == null) return;
            foreach (var p in o.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanWrite) continue;   // computed properties follow their sources
                var name = prefix + (prefix.Length == 0 ? "" : ".") + char.ToLowerInvariant(p.Name[0]) + p.Name.Substring(1);
                var v = p.GetValue(o);
                if (v is string str)
                {
                    if (str.Any(ch => char.IsControl(ch))) r.Error(name, "contains a control character");
                }
                else if (v is System.Collections.IList list)
                {
                    for (int i = 0; i < list.Count; i++) ControlCharacters(r, name + "[" + i + "]", list[i]);
                }
                else if (v != null && p.PropertyType.IsClass && p.PropertyType.Namespace == typeof(DeploymentConfig).Namespace)
                    ControlCharacters(r, name, v);
            }
        }

        private static void Account(ValidationResult r, string path, string account)
        {
            if (string.IsNullOrWhiteSpace(account) || !AccountRx.IsMatch(account.Trim())) { r.Error(path, "use DOMAIN\\name (a gMSA ends with $) or name@domain"); return; }
            if (!DeploymentConfig.IsGmsa(account)) r.Warn(path, "not a group managed service account: its password will be asked at install time and must be renewed when it expires (a gMSA, DOMAIN\\name$, avoids this)");
        }

        private static void Path(ValidationResult r, string path, string value, bool allowRoot)
        {
            if (string.IsNullOrWhiteSpace(value) || !AbsWinPathRx.IsMatch(value.Trim())) r.Error(path, "must be an absolute Windows path like C:\\vHA\\Bin");
            else if (value.Contains("..")) r.Error(path, "'..' is not allowed");
        }

        private static void Duplicates(ValidationResult r, string path, IEnumerable<string> values, string what)
        {
            foreach (var g in values.Where(v => !string.IsNullOrWhiteSpace(v)).GroupBy(v => v.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                r.Error(path, "duplicated " + what + " '" + g.Key + "'");
        }

        internal static bool IsFqdnOrShort(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length > 253) return false;
            return s.Split('.').All(l => HostLabelRx.IsMatch(l));
        }

        internal static bool IsHostOrIp(string s) => !string.IsNullOrWhiteSpace(s) && (IsFqdnOrShort(s) || (IPAddress.TryParse(s, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && s.Split('.').Length == 4));
    }
}
