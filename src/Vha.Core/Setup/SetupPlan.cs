using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vha.Core.Util;

namespace Vha.Core.Setup
{
    public enum StepKind { Prerequisite, Files, Service, Task, Database, Cluster, Verify }

    public sealed class SetupStep
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public StepKind Kind { get; set; }
        /// <summary>PowerShell executed with the host variables listed in <see cref="Variables"/>. Null for steps implemented in C# (database, rights).</summary>
        public string Script { get; set; }
        /// <summary>Names of host variables the script needs (e.g. svcPassword); values are never part of the script text.</summary>
        public List<string> Variables { get; } = new List<string>();
        public string AgName { get; set; }
        public override string ToString() => Id + " - " + Title;
    }

    public sealed class SetupOptions
    {
        public bool ForceCleanInstall { get; set; }
    }

    /// <summary>
    /// The ordered list of steps that bring one node to the configured state. Every step is idempotent: running the setup twice,
    /// or after a failure, converges to the same result. Scripts get secrets only through host variables.
    /// </summary>
    public static class SetupPlanBuilder
    {
        public const string RebootMarker = "##REBOOT##";

        public static List<SetupStep> BuildNodePlan(DeploymentConfig c, NodeSettings node)
        {
            var steps = new List<SetupStep>();
            steps.Add(new SetupStep { Id = "os", Title = "Check operating system, PowerShell and administrator rights", Kind = StepKind.Prerequisite, Script = Scripts.CheckOs });
            var dn = new SetupStep { Id = "dotnet", Title = ".NET Framework 4.8", Kind = StepKind.Prerequisite, Script = Scripts.DotNet }; dn.Variables.Add("prereqDir"); steps.Add(dn);
            steps.Add(new SetupStep { Id = "features", Title = "Failover Clustering feature and PowerShell remoting", Kind = StepKind.Prerequisite, Script = Scripts.Features });
            var pc = new SetupStep { Id = "powercli", Title = "VMware PowerCLI module", Kind = StepKind.Prerequisite, Script = Scripts.PowerCli(c.VCenter.AllowUntrustedCertificate) }; pc.Variables.Add("prereqDir"); steps.Add(pc);
            steps.Add(new SetupStep { Id = "admin", Title = "Service account: member of the local Administrators", Kind = StepKind.Prerequisite, Script = MoreScripts.LocalAdministrator(c.ServiceAccount) });
            steps.Add(new SetupStep { Id = "rights", Title = "Logon rights: 'Log on as a service' (service account) and 'Log on as a batch job' (task account)", Kind = StepKind.Prerequisite });   // C#: secedit with tested INF editing
            var fl = new SetupStep { Id = "files", Title = "Copy program files", Kind = StepKind.Files, Script = Scripts.Files(c) }; fl.Variables.Add("packageDir"); steps.Add(fl);
            foreach (var ag in c.AvailabilityGroups)
            {
                steps.Add(new SetupStep { Id = "ini:" + ag.Name, Title = "jelena.ini for " + ag.Name, Kind = StepKind.Files, AgName = ag.Name, Script = Scripts.Ini(c, ag, node) });
                var sv = new SetupStep { Id = "service:" + ag.Name, Title = "Windows service " + ag.ServiceName, Kind = StepKind.Service, AgName = ag.Name, Script = Scripts.Service(c, ag) };
                if (!DeploymentConfig.IsGmsa(c.ServiceAccount)) sv.Variables.Add("svcPassword");
                steps.Add(sv);
                var tk = new SetupStep { Id = "task:" + ag.Name, Title = "Scheduled task '" + ag.EffectiveTaskName + "'", Kind = StepKind.Task, AgName = ag.Name, Script = Scripts.Task(c, ag, node, c.AvailabilityGroups.IndexOf(ag)) };
                if (!DeploymentConfig.IsGmsa(c.TaskAccount)) tk.Variables.Add("taskPassword");
                steps.Add(tk);
                steps.Add(new SetupStep { Id = "disksign:" + ag.Name, Title = "disksign.ini on the mount points of " + ag.Name, Kind = StepKind.Files, AgName = ag.Name, Script = Scripts.DiskSign(ag) });
                steps.Add(new SetupStep { Id = "database:" + ag.Name, Title = "Jelena database, settings, disks and databases of " + ag.Name, Kind = StepKind.Database, AgName = ag.Name });   // C#
            }
            return steps;
        }

        /// <summary>Cluster creation (first node only) and the roles whose initial owner is this node.</summary>
        public static List<SetupStep> BuildClusterPlan(DeploymentConfig c, NodeSettings node)
        {
            var steps = new List<SetupStep>();
            var first = c.Nodes[0];
            if (string.Equals(first.VmName, node.VmName, StringComparison.OrdinalIgnoreCase))
                steps.Add(new SetupStep { Id = "cluster", Title = "Create the failover cluster " + c.Cluster.Name + " (no storage) and its quorum witness", Kind = StepKind.Cluster, Script = Scripts.CreateCluster(c) });
            foreach (var ag in c.AvailabilityGroups.Where(a => string.Equals(a.InitialOwner, node.VmName, StringComparison.OrdinalIgnoreCase)))
                steps.Add(new SetupStep { Id = "role:" + ag.Name, Title = "Cluster role for " + ag.Name + " (generic service " + ag.ServiceName + ")", Kind = StepKind.Cluster, AgName = ag.Name, Script = Scripts.Role(c, ag) });
            return steps;
        }

        public static List<SetupStep> BuildVerifyPlan(DeploymentConfig c)
        {
            return c.AvailabilityGroups.Select(ag => new SetupStep { Id = "check:" + ag.Name, Title = "ClusteredService.exe --check " + ag.ServiceName, Kind = StepKind.Verify, AgName = ag.Name, Script = Scripts.Check(c, ag) }).ToList();
        }

        public static string Describe(IEnumerable<SetupStep> steps, bool withScripts)
        {
            var sb = new StringBuilder(); int i = 1;
            foreach (var s in steps)
            {
                sb.AppendLine(i++.ToString("00") + ". [" + s.Kind + "] " + s.Title + (s.Script == null ? "  (built-in action)" : ""));
                if (withScripts && s.Script != null) { foreach (var l in s.Script.Replace("\r\n", "\n").Split('\n')) sb.AppendLine("      | " + l); }
            }
            return sb.ToString();
        }
    }

    /// <summary>The PowerShell text of the steps. User supplied values are always embedded through <see cref="PsQuote"/>.</summary>
    public static class Scripts
    {
        private static string Q(string s) => PsQuote.Single(s);
        private static string B(bool b) => b ? "$true" : "$false";

        public const string CheckOs = @"
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run the setup from an elevated (Run as administrator) session.' }
if ($PSVersionTable.PSVersion -lt [version]'5.1') { throw ('Windows PowerShell 5.1 or later is required (found ' + $PSVersionTable.PSVersion + ').') }
$os = Get-CimInstance Win32_OperatingSystem
if ([version]$os.Version -lt [version]'10.0') { throw ('Windows Server 2016 or later is required (found ' + $os.Caption + ' ' + $os.Version + ').') }
if ($os.ProductType -eq 1) { throw ('A server operating system is required (found ' + $os.Caption + ').') }
('OS: ' + $os.Caption + ' ' + $os.Version + ', PowerShell ' + $PSVersionTable.PSVersion)
";

        public const string DotNet = @"
$release = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
if ($release -ge 528040) { ('.NET Framework 4.8 or later is installed (release ' + $release + ')'); return }
$installer = Join-Path $prereqDir 'ndp48-x86-x64-allos-enu.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw ('.NET Framework 4.8 is missing and ' + $installer + ' was not found. Download the offline installer from https://go.microsoft.com/fwlink/?linkid=2088631 into the prerequisites folder.') }
$sig = Get-AuthenticodeSignature -LiteralPath $installer
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike '*Microsoft Corporation*') { throw ('The signature of ' + $installer + ' is not a valid Microsoft signature (' + $sig.Status + ').') }
$p = Start-Process -FilePath $installer -ArgumentList '/q','/norestart' -Wait -PassThru
if ($p.ExitCode -eq 3010 -or $p.ExitCode -eq 1641) { '" + SetupPlanBuilder.RebootMarker + @" .NET Framework 4.8 installed, restart the server and run the setup again'; return }
if ($p.ExitCode -ne 0) { throw ('The .NET Framework installer failed with exit code ' + $p.ExitCode) }
'.NET Framework 4.8 installed'
";

        public const string Features = @"
$f = Get-WindowsFeature -Name Failover-Clustering
if (-not $f) { throw 'Get-WindowsFeature returned nothing: is this a Windows Server?' }
if (-not $f.Installed) {
    $r = Install-WindowsFeature -Name Failover-Clustering -IncludeManagementTools
    if (-not $r.Success) { throw 'Installation of the Failover Clustering feature failed' }
    if ([string]$r.RestartNeeded -ne 'No') { '" + SetupPlanBuilder.RebootMarker + @" Failover Clustering installed, restart the server and run the setup again'; return }
    'Failover Clustering installed'
} else { 'Failover Clustering already installed' }
Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
'PowerShell remoting enabled'
";

        public static string PowerCli(bool allowUntrusted) => @"
$min = [version]'12.0'
$m = Get-Module -ListAvailable -Name VMware.PowerCLI | Sort-Object Version -Descending | Select-Object -First 1
if (-not $m -or $m.Version -lt $min) {
    $offline = Join-Path $prereqDir 'PowerCLI'
    if (Test-Path -LiteralPath $offline) {
        $target = Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'
        Get-ChildItem -LiteralPath $offline -Directory | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse -Force }
        'PowerCLI copied from the offline folder'
    } else {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force | Out-Null
        Install-Module -Name VMware.PowerCLI -MinimumVersion 12.0 -Scope AllUsers -Force -AllowClobber
        'PowerCLI installed from the PowerShell Gallery'
    }
    $m = Get-Module -ListAvailable -Name VMware.PowerCLI | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $m -or $m.Version -lt $min) { throw 'VMware.PowerCLI 12 or later is still not available after the installation' }
}
Import-Module VMware.VimAutomation.Core -ErrorAction Stop
Set-PowerCLIConfiguration -Scope AllUsers -ParticipateInCEIP $false -InvalidCertificateAction " + (allowUntrusted ? "Ignore" : "Fail") + @" -Confirm:$false | Out-Null
('VMware.PowerCLI ' + $m.Version + ' ready (certificate validation: " + (allowUntrusted ? "OFF" : "on") + @")')
";

        public static string Files(DeploymentConfig c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$install = " + Q(c.InstallDirectory.TrimEnd('\\')));
            sb.AppendLine("$src = Join-Path $packageDir 'bin'");
            sb.AppendLine("if (-not (Test-Path -LiteralPath (Join-Path $src 'ClusteredService\\ClusteredService.exe'))) { throw ('Program files not found under ' + $src) }");
            sb.AppendLine("function Copy-Verified([string]$From, [string]$To) {");
            sb.AppendLine("    New-Item -ItemType Directory -Force -Path $To | Out-Null");
            sb.AppendLine("    foreach ($f in Get-ChildItem -LiteralPath $From -File) {");
            sb.AppendLine("        $dest = Join-Path $To $f.Name");
            sb.AppendLine("        if ((Test-Path -LiteralPath $dest) -and ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash)) { continue }");
            sb.AppendLine("        Copy-Item -LiteralPath $f.FullName -Destination $dest -Force");
            sb.AppendLine("        if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash) { throw ('Copy of ' + $f.Name + ' to ' + $To + ' is corrupt') }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("New-Item -ItemType Directory -Force -Path $install | Out-Null");
            sb.AppendLine("Copy-Verified (Join-Path $src 'CsuCmd') $install");
            foreach (var ag in c.AvailabilityGroups)
            {
                sb.AppendLine("$dir = " + Q(NodeNaming.AgDirectory(c, ag)));
                sb.AppendLine("$svc = Get-Service -Name " + Q(ag.ServiceName) + " -ErrorAction SilentlyContinue");
                sb.AppendLine("if ($svc -and $svc.Status -ne 'Stopped') { throw ('The service ' + $svc.Name + ' is running (it is owned by the cluster role): take the role offline before updating the program files.') }");
                sb.AppendLine("Copy-Verified (Join-Path $src 'ClusteredService') $dir");
                sb.AppendLine("New-Item -ItemType Directory -Force -Path (Join-Path $dir " + Q(ag.ServiceName + "-Log") + ") | Out-Null");
            }
            sb.AppendLine("'Program files installed in ' + $install");
            return sb.ToString();
        }

        public static string Ini(DeploymentConfig c, AgSettings ag, NodeSettings node)
        {
            return "$path = " + Q(NodeNaming.AgDirectory(c, ag) + "\\jelena.ini") + "\r\n"
                 + "$new = " + Q(IniRenderer.Render(c, ag, node)) + "\r\n"
                 + "New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null\r\n"
                 + "if (Test-Path -LiteralPath $path) {\r\n"
                 + "    $old = [IO.File]::ReadAllText($path)\r\n"
                 + "    if ($old -ceq $new) { 'jelena.ini unchanged'; return }\r\n"
                 + "    Copy-Item -LiteralPath $path -Destination ($path + '.bak-' + (Get-Date -Format yyyyMMddHHmmss))\r\n"
                 + "}\r\n"
                 + "[IO.File]::WriteAllText($path, $new, (New-Object System.Text.UTF8Encoding $false))\r\n"
                 + "'jelena.ini written'\r\n";
        }

        public static string Service(DeploymentConfig c, AgSettings ag)
        {
            var gmsa = DeploymentConfig.IsGmsa(c.ServiceAccount);
            return "$name = " + Q(ag.ServiceName) + "\r\n"
                 + "$bin = '\"' + " + Q(NodeNaming.AgDirectory(c, ag) + "\\ClusteredService.exe") + " + '\"'\r\n"
                 + "$acct = " + Q(c.ServiceAccount.Trim()) + "\r\n"
                 + "$display = " + Q("Jelena clustered service for Availability Group " + ag.Name) + "\r\n"
                 + "$exists = [bool](Get-Service -Name $name -ErrorAction SilentlyContinue)\r\n"
                 + "$verb = if ($exists) { 'config' } else { 'create' }\r\n"
                 + "$a = @($verb, $name, 'binPath=', $bin, 'start=', 'demand', 'obj=', $acct, 'DisplayName=', $display)\r\n"
                 + (gmsa ? "" : "$a += @('password=', $svcPassword)\r\n")
                 + "$out = & sc.exe @a\r\n"
                 + "if ($LASTEXITCODE -ne 0) { throw ('sc.exe ' + $verb + ' failed (' + $LASTEXITCODE + '): ' + ($out -join ' ')) }\r\n"
                 + "& sc.exe description $name $display | Out-Null\r\n"
                 + "$cfg = Get-CimInstance Win32_Service -Filter (\"Name='\" + $name.Replace(\"'\", \"''\") + \"'\")\r\n"
                 + "if (-not $cfg) { throw 'The service was not created' }\r\n"
                 + "if ($cfg.StartName -ne $acct) { throw ('The service runs as ' + $cfg.StartName + ' instead of ' + $acct) }\r\n"
                 + "('service ' + $name + ' ' + $verb + 'd, account ' + $cfg.StartName + ', start mode ' + $cfg.StartMode)\r\n";
        }

        public static string Task(DeploymentConfig c, AgSettings ag, NodeSettings node, int agIndex)
        {
            var gmsa = DeploymentConfig.IsGmsa(c.TaskAccount);
            return "$name = " + Q(ag.EffectiveTaskName) + "\r\n"
                 + "$acct = " + Q(c.TaskAccount.Trim()) + "\r\n"
                 + "$xml = " + Q(TaskXmlRenderer.Render(c, agIndex, node)) + "\r\n"
                 + "if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $name -Confirm:$false }\r\n"
                 + (gmsa ? "Register-ScheduledTask -TaskName $name -Xml $xml -User $acct | Out-Null\r\n" : "Register-ScheduledTask -TaskName $name -Xml $xml -User $acct -Password $taskPassword | Out-Null\r\n")
                 + "$t = Get-ScheduledTask -TaskName $name -ErrorAction Stop\r\n"
                 + "('task ' + $name + ' registered, state ' + $t.State + ', runs as ' + $t.Principal.UserId)\r\n";
        }

        public static string DiskSign(AgSettings ag)
        {
            return "$expected = " + Q(ag.Name) + "\r\n"
                 + "$root = " + Q(ag.MountRoot.TrimEnd('\\')) + "\r\n"
                 + "$done = 0\r\n"
                 + "foreach ($sub in 'Data','Log') {\r\n"
                 + "    $mount = Join-Path $root $sub\r\n"
                 + "    if (-not (Test-Path -LiteralPath $mount)) { ('mount point ' + $mount + ' is not present on this node (the disks are attached to another node)'); continue }\r\n"
                 + "    $dir = Join-Path $mount 'jelena'\r\n"
                 + "    $file = Join-Path $dir 'disksign.ini'\r\n"
                 + "    if (Test-Path -LiteralPath $file) {\r\n"
                 + "        $text = [IO.File]::ReadAllText($file)\r\n"
                 + "        if ($text -match '(?im)^\\s*Name\\s*=\\s*(.*?)\\s*$') {\r\n"
                 + "            if ($Matches[1] -ne $expected) { throw ($file + ' declares the availability group ' + $Matches[1] + ': this disk belongs to another AG, refusing to claim it for ' + $expected) }\r\n"
                 + "            continue\r\n"
                 + "        }\r\n"
                 + "    }\r\n"
                 + "    New-Item -ItemType Directory -Force -Path $dir | Out-Null\r\n"
                 + "    $content = \"[AG]`r`nName=\" + $expected + \"`r`n\"\r\n"
                 + "    if (Test-Path -LiteralPath $file) { $content = $content + [IO.File]::ReadAllText($file) }\r\n"
                 + "    [IO.File]::WriteAllText($file, $content, (New-Object System.Text.UTF8Encoding $false))\r\n"
                 + "    $done++\r\n"
                 + "}\r\n"
                 + "('disksign.ini written on ' + $done + ' mount point(s)')\r\n";
        }

        public static string CreateCluster(DeploymentConfig c)
        {
            var nodes = string.Join(",", c.Nodes.Select(n => Q(n.DnsName)));
            var sb = new StringBuilder();
            sb.AppendLine("Import-Module FailoverClusters -ErrorAction Stop");
            sb.AppendLine("$name = " + Q(c.Cluster.Name));
            sb.AppendLine("$nodes = @(" + nodes + ")");
            sb.AppendLine("$cl = Get-Cluster -Name $name -ErrorAction SilentlyContinue");
            sb.AppendLine("if (-not $cl) {");
            sb.AppendLine("    New-Cluster -Name $name -Node $nodes -StaticAddress " + Q(c.Cluster.IpAddress) + " -NoStorage -ErrorAction Stop | Out-Null");
            sb.AppendLine("    'cluster ' + $name + ' created'");
            sb.AppendLine("} else { 'cluster ' + $name + ' already exists' }");
            sb.AppendLine("foreach ($n in $nodes) { if (-not (Get-ClusterNode -Cluster $name -Name ($n.Split('.')[0]) -ErrorAction SilentlyContinue)) { Add-ClusterNode -Cluster $name -Name $n -NoStorage -ErrorAction Stop | Out-Null; 'node ' + $n + ' added' } }");
            if (c.Cluster.Witness == WitnessType.FileShare)
            {
                sb.AppendLine("Set-ClusterQuorum -Cluster $name -FileShareWitness " + Q(c.Cluster.WitnessPath) + " -ErrorAction Stop | Out-Null");
                sb.AppendLine("'quorum: file share witness ' + " + Q(c.Cluster.WitnessPath));
            }
            sb.AppendLine("$q = Get-ClusterQuorum -Cluster $name; ('quorum type: ' + $q.QuorumType)");
            return sb.ToString();
        }

        public static string Role(DeploymentConfig c, AgSettings ag)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Import-Module FailoverClusters -ErrorAction Stop");
            sb.AppendLine("$cluster = " + Q(c.Cluster.Name) + "; $ag = " + Q(ag.Name) + "; $svc = " + Q(ag.ServiceName));
            sb.AppendLine("$existing = Get-ClusterGroup -Cluster $cluster -Name $ag -ErrorAction SilentlyContinue");
            sb.AppendLine("if ($existing) { 'cluster role ' + $ag + ' already exists (' + $existing.State + ')'; return }");
            sb.AppendLine("# The role is created from the node that holds the disks, so the first start of the service finds them already attached.");
            sb.AppendLine("$g = Add-ClusterGenericServiceRole -Cluster $cluster -ServiceName $svc -Name " + Q(ag.ListenerName) + " -StaticAddress " + Q(ag.ListenerIp) + " -ErrorAction Stop");
            sb.AppendLine("$g.Name = $ag     # the service moves the group by the availability group name");
            if (c.Cluster.FailoverThreshold > 0)
            {
                sb.AppendLine("$g.FailoverThreshold = " + c.Cluster.FailoverThreshold);
                sb.AppendLine("$g.FailoverPeriod = " + c.Cluster.FailoverPeriodHours);
            }
            sb.AppendLine("$g = Get-ClusterGroup -Cluster $cluster -Name $ag");
            sb.AppendLine("('cluster role ' + $ag + ' created, state ' + $g.State + ', owner ' + $g.OwnerNode)");
            return sb.ToString();
        }

        public static string Check(DeploymentConfig c, AgSettings ag)
        {
            return "$exe = " + Q(NodeNaming.AgDirectory(c, ag) + "\\ClusteredService.exe") + "\r\n"
                 + "$out = & $exe '--check' " + Q(ag.ServiceName) + " 2>&1\r\n"
                 + "$code = $LASTEXITCODE\r\n"
                 + "$out | ForEach-Object { [string]$_ }\r\n"
                 + "if ($code -ne 0) { throw ('ClusteredService.exe --check reported problems (exit code ' + $code + ')') }\r\n";
        }
    }
}

namespace Vha.Core.Setup
{
    public static class MoreScripts
    {
        public static string LocalAdministrator(string account) =>
            "$acct = " + PsQuote.Single(account.Trim()) + "\r\n"
          + "$members = @(Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop | ForEach-Object { $_.Name })\r\n"
          + "if ($members -contains $acct) { $acct + ' is already a local administrator' }\r\n"
          + "else { Add-LocalGroupMember -Group 'Administrators' -Member $acct -ErrorAction Stop; $acct + ' added to the local Administrators group' }\r\n";

        public static string Uninstall(DeploymentConfig c, AgSettings ag) =>
            "$svc = " + PsQuote.Single(ag.ServiceName) + "; $task = " + PsQuote.Single(ag.EffectiveTaskName) + "\r\n"
          + "if (Get-ClusterGroup -Name " + PsQuote.Single(ag.Name) + " -ErrorAction SilentlyContinue) { throw 'The cluster role " + ag.Name.Replace("'", "''") + " still exists: remove it from Failover Cluster Manager first' }\r\n"
          + "$s = Get-Service -Name $svc -ErrorAction SilentlyContinue\r\n"
          + "if ($s) { if ($s.Status -ne 'Stopped') { throw ('The service ' + $svc + ' is running') }; & sc.exe delete $svc | Out-Null; if ($LASTEXITCODE -ne 0) { throw ('sc.exe delete failed (' + $LASTEXITCODE + ')') }; 'service ' + $svc + ' deleted' } else { 'service ' + $svc + ' not present' }\r\n"
          + "if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $task -Confirm:$false; 'task ' + $task + ' removed' } else { 'task ' + $task + ' not present' }\r\n"
          + "'Program files, the Jelena database and the disksign.ini files are left untouched.'\r\n";
    }
}
