using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vha.Core.Logging;
using Vha.Core.Setup;
using Vha.Infrastructure.Setup;

namespace VhaSetup
{
    internal static class Program
    {
        private const string Usage = @"vHA setup 3.0

  VhaSetup wizard    [--config file]                       interactive configurator: writes the deployment file
  VhaSetup validate  --config file                         checks the deployment file
  VhaSetup plan      --config file [--node vm] [--scripts] shows what 'install' would do on a node
  VhaSetup install   --config file [--node vm] [--package dir] [--prereq dir] [--yes] [--dry-run]
  VhaSetup cluster   --config file [--node vm] [--dry-run] creates the cluster / the roles owned by this node
  VhaSetup check     --config file                         runs ClusteredService.exe --check for every availability group
  VhaSetup uninstall --config file [--node vm] [--yes]     removes services and tasks (files and databases are kept)

  --config   deployment file (default: vha-deployment.json in the current folder)
  --node     VM name of this machine as written in the file (default: detected from the computer name)
  --package  folder of the installation package (default: the parent of the folder of VhaSetup.exe)
  --prereq   folder with the offline prerequisites (default: <package>\prereq)
Passwords are taken from VHA_SERVICE_PASSWORD, VHA_TASK_PASSWORD, VHA_VCENTER_PASSWORD or asked (only for accounts that are not gMSA).
Exit codes: 0 ok, 1 a step failed, 2 invalid usage or configuration, 3 restart required (run the same command again).";

        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (OperationCanceledException) { Console.Error.WriteLine("Annullato."); return 2; }
            catch (Exception ex) { Console.Error.WriteLine("ERRORE: " + ex.Message); return 2; }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "/?") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            var cmd = args[0].ToLowerInvariant();
            var o = CliOptions.Parse(args.Skip(1).ToArray());
            var config = o.Get("config") ?? "vha-deployment.json";

            if (cmd == "wizard")
            {
                var existing = File.Exists(config) ? DeploymentConfigJson.Load(config) : null;
                if (existing != null) Console.WriteLine("Carico " + config + ": i valori attuali sono i predefiniti.");
                var c = new Wizard(new SystemConsole()).Run(existing);
                DeploymentConfigJson.Save(c, config);
                Console.WriteLine("\nConfigurazione salvata in " + Path.GetFullPath(config));
                Console.WriteLine("Prossimo passo, su OGNI nodo (prompt amministratore): VhaSetup install --config \"" + config + "\"");
                return 0;
            }

            if (!File.Exists(config)) { Console.Error.WriteLine("File di configurazione non trovato: " + config + " (usa 'VhaSetup wizard')"); return 2; }
            var cfg = DeploymentConfigJson.Load(config);
            var v = DeploymentValidator.Validate(cfg);
            foreach (var i in v.Issues) Console.WriteLine(i);
            if (!v.IsValid) { Console.Error.WriteLine("\nLa configurazione non è valida."); return 2; }
            if (cmd == "validate") { Console.WriteLine("\nConfigurazione valida."); return 0; }

            var node = NodeResolver.Resolve(cfg, o.Get("node"), Environment.MachineName);
            var package = Path.GetFullPath(o.Get("package") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            var prereq = Path.GetFullPath(o.Get("prereq") ?? Path.Combine(package, "prereq"));
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "vHA", "Setup");
            var log = new CompositeLog(new ConsoleLog(), new FileLog(logDir));
            Console.WriteLine("\nNodo: " + node.VmName + " (" + node.DnsName + ")");

            List<SetupStep> plan;
            switch (cmd)
            {
                case "plan":
                    Console.WriteLine(SetupPlanBuilder.Describe(SetupPlanBuilder.BuildNodePlan(cfg, node), o.Has("scripts")));
                    Console.WriteLine("Poi, dal nodo " + cfg.Nodes[0].VmName + " e da ogni proprietario iniziale di un AG: VhaSetup cluster");
                    return 0;
                case "install": plan = SetupPlanBuilder.BuildNodePlan(cfg, node); break;
                case "cluster": plan = SetupPlanBuilder.BuildClusterPlan(cfg, node); break;
                case "check": plan = SetupPlanBuilder.BuildVerifyPlan(cfg); break;
                case "uninstall": plan = cfg.AvailabilityGroups.Select(a => new SetupStep { Id = "uninstall:" + a.Name, Title = "Remove service and task of " + a.Name, Kind = StepKind.Service, AgName = a.Name, Script = MoreScripts.Uninstall(cfg, a) }).ToList(); break;
                default: Console.Error.WriteLine("Comando sconosciuto: " + cmd + "\n\n" + Usage); return 2;
            }
            if (plan.Count == 0) { Console.WriteLine("Niente da fare su questo nodo."); return 0; }

            Console.WriteLine(SetupPlanBuilder.Describe(plan, false));
            if (!o.Has("yes") && !o.Has("dry-run") && !Confirm("Procedere?")) return 2;

            var secrets = new EnvironmentOrPromptSecrets(PromptPassword);
            var options = new RunnerOptions { PackageDir = package, PrereqDir = prereq, DryRun = o.Has("dry-run"), ShowScripts = o.Has("dry-run") };
            var db = new DatabaseSetup(Path.Combine(package, "sql"), log, new DpapiProtector());
            var report = new SetupRunner(cfg, node, options, log, secrets, db).Run(plan);

            Console.WriteLine();
            if (report.RebootRequired) { Console.WriteLine("RIAVVIO NECESSARIO: riavvia il server e ripeti lo stesso comando."); return 3; }
            if (!report.Success) { Console.WriteLine("Il setup si è fermato a un errore (vedi sopra). Dopo la correzione ripeti lo stesso comando: i passi già fatti non vengono rifatti."); return 1; }
            Console.WriteLine(options.DryRun ? "Simulazione terminata: niente è stato modificato." : "Completato.");
            if (cmd == "install" && !options.DryRun)
                Console.WriteLine("Prossimi passi: installa tutti i nodi, poi 'VhaSetup cluster' sul nodo " + cfg.Nodes[0].VmName + " e su ogni proprietario iniziale di un AG, infine 'VhaSetup check'.");
            return 0;
        }

        private static bool Confirm(string question)
        {
            Console.Write(question + " (s/n) [n]: ");
            var a = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            return a == "s" || a == "si" || a == "sì" || a == "y" || a == "yes";
        }

        private static string PromptPassword(string prompt)
        {
            Console.Write(prompt + ": ");
            var sb = new StringBuilder();
            while (true)
            {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                if (k.KeyChar != '\0') sb.Append(k.KeyChar);
            }
            if (sb.Length == 0) throw new InvalidOperationException("Empty password");
            return sb.ToString();
        }

        private sealed class SystemConsole : IConsoleIO
        {
            public void WriteLine(string text) => Console.WriteLine(text);
            public string ReadLine() => Console.ReadLine();
        }

        private sealed class ConsoleLog : ILog
        {
            public void Debug(string m) { }
            public void Info(string m) => Console.WriteLine(m);
            public void Warn(string m) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("ATTENZIONE: " + m); Console.ResetColor(); }
            public void Error(string m) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(m); Console.ResetColor(); }
        }
    }
}
