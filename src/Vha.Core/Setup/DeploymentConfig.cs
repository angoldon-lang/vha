using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vha.Core.Setup
{
    /// <summary>
    /// Declarative description of a whole vHA deployment (one cluster, any number of availability groups).
    /// It is the single input of the setup: nothing secret is stored in it (vCenter and service account passwords are
    /// asked at install time and protected with DPAPI).
    /// </summary>
    public sealed class DeploymentConfig
    {
        public int SchemaVersion { get; set; } = 1;
        /// <summary>Where ClusteredService.exe/CsuCmd.exe are installed on every node.</summary>
        public string InstallDirectory { get; set; } = @"C:\vHA\Bin";
        public VCenterSettings VCenter { get; set; } = new VCenterSettings();
        /// <summary>Account of the Windows service "Jelena-&lt;AG&gt;" (local administrator, sysadmin on the instance). A gMSA (name ending with $) is recommended.</summary>
        public string ServiceAccount { get; set; } = "";
        /// <summary>Account of the scheduled task that runs CsuCmd.exe ("Log on as a batch job").</summary>
        public string TaskAccount { get; set; } = "";
        public ClusterSettings Cluster { get; set; } = new ClusterSettings();
        public List<NodeSettings> Nodes { get; set; } = new List<NodeSettings>();
        public List<AgSettings> AvailabilityGroups { get; set; } = new List<AgSettings>();

        public static bool IsGmsa(string account) => !string.IsNullOrEmpty(account) && account.TrimEnd().EndsWith("$", StringComparison.Ordinal);
    }

    public sealed class VCenterSettings
    {
        public string Server { get; set; } = "";
        /// <summary>vCenter user (DOMAIN\user or user@domain). Empty = the service account is used (pass-through authentication).</summary>
        public string User { get; set; } = "";
        /// <summary>false (default): the vCenter certificate must be trusted by the nodes. true sets PowerCLI InvalidCertificateAction=Ignore (not recommended).</summary>
        public bool AllowUntrustedCertificate { get; set; }
    }

    public enum WitnessType { None, FileShare }

    public sealed class ClusterSettings
    {
        public string Name { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public WitnessType Witness { get; set; } = WitnessType.FileShare;
        /// <summary>UNC path of the witness share (WitnessType = FileShare).</summary>
        public string WitnessPath { get; set; } = "";
        /// <summary>Optional: maximum failures of the clustered role in <see cref="FailoverPeriodHours"/> hours. 0 = leave the Windows defaults.</summary>
        public int FailoverThreshold { get; set; }
        public int FailoverPeriodHours { get; set; }
    }

    public sealed class NodeSettings
    {
        /// <summary>Name of the virtual machine in vCenter.</summary>
        public string VmName { get; set; } = "";
        /// <summary>DNS name of the machine (used to reach SQL Server and for PowerShell remoting).</summary>
        public string DnsName { get; set; } = "";
    }

    public enum OwnerPolicy { Force, Abort }

    public sealed class AgSettings
    {
        /// <summary>Availability group name, e.g. isql2025a-AG01 (letters, digits, '.', '_' and '-').</summary>
        public string Name { get; set; } = "";
        /// <summary>Named instance, identical on every node. Empty = default instance.</summary>
        public string InstanceName { get; set; } = "";
        /// <summary>TCP port when the instance does not use the SQL Browser (optional).</summary>
        public int SqlPort { get; set; }
        /// <summary>Root of the mount points of the AG, e.g. M:\isql2025a-AG01 (folders Data and Log below it).</summary>
        public string MountRoot { get; set; } = "";
        /// <summary>Wildcard filter on the VMDK file names of the AG disks ("[datastore] folder/file.vmdk"), e.g. *isql2025a-AG01*.</summary>
        public string DiskNameFilter { get; set; } = "";
        /// <summary>VM that holds the disks at installation time (and becomes the initial owner). The cluster role is created from this node.</summary>
        public string InitialOwner { get; set; } = "";
        /// <summary>Client access point (NetBIOS name, max 15 characters) clients use to reach the AG, e.g. ISQL25A01.</summary>
        public string ListenerName { get; set; } = "";
        public string ListenerIp { get; set; } = "";
        public bool ReservationEnabled { get; set; }
        public int ReservationCpuMHz { get; set; }
        public int ReservationMemMB { get; set; }
        public OwnerPolicy OwnerUnreachablePolicy { get; set; } = OwnerPolicy.Force;
        public int CsuWaitSeconds { get; set; } = 120;
        public int FailoverTimeoutSeconds { get; set; } = 900;
        /// <summary>Extra connection options, e.g. "Encrypt=True;TrustServerCertificate=True" (required by SQL Server 2022/2025 when the certificate is self signed).</summary>
        public string SqlOptions { get; set; } = "Encrypt=True;TrustServerCertificate=True";
        /// <summary>1 = errors and warnings, 2 = info (default), 3 = debug.</summary>
        public int CsuLogLevel { get; set; } = 2;
        public int CsuIntervalSeconds { get; set; } = 60;
        public bool CreateDummyDatabase { get; set; } = true;
        public string DummyDatabaseName { get; set; } = "Dummy01";
        /// <summary>Name of the scheduled task. Empty = "CSU - &lt;instance&gt; - &lt;AG&gt;".</summary>
        public string TaskName { get; set; } = "";

        public string EffectiveTaskName => string.IsNullOrWhiteSpace(TaskName) ? "CSU - " + (string.IsNullOrEmpty(InstanceName) ? "default" : InstanceName) + " - " + Name : TaskName;
        public string ServiceName => "Jelena-" + Name;
    }

    public static class DeploymentConfigJson
    {
        private static JsonSerializerOptions Options()
        {
            var o = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow   // a mistyped key must be an error, not silently ignored
            };
            o.Converters.Add(new JsonStringEnumConverter());
            return o;
        }

        public static string Serialize(DeploymentConfig c) => JsonSerializer.Serialize(c, Options());

        public static DeploymentConfig Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("The configuration file is empty");
            try { return JsonSerializer.Deserialize<DeploymentConfig>(json, Options()) ?? throw new FormatException("The configuration file is empty"); }
            catch (JsonException ex) { throw new FormatException("Invalid configuration file: " + ex.Message, ex); }
        }

        public static DeploymentConfig Load(string path) => Parse(System.IO.File.ReadAllText(path));
        public static void Save(DeploymentConfig c, string path) { System.IO.File.WriteAllText(path, Serialize(c), new System.Text.UTF8Encoding(false)); }
    }
}
