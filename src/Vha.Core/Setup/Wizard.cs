using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vha.Core.Setup
{
    public interface IConsoleIO
    {
        void WriteLine(string text);
        /// <summary>Reads a line; null at end of input.</summary>
        string ReadLine();
    }

    /// <summary>
    /// Interactive configurator. Every answer is validated immediately with the same rules used by the setup
    /// (<see cref="DeploymentValidator"/>), so a configuration produced here cannot be rejected later for a typo.
    /// </summary>
    public sealed class Wizard
    {
        private readonly IConsoleIO _io;
        private DeploymentConfig _c;
        public const int MaxAttempts = 20;

        public Wizard(IConsoleIO io) { _io = io; }

        public DeploymentConfig Run(DeploymentConfig existing = null)
        {
            _c = existing ?? new DeploymentConfig();
            _io.WriteLine("=== Configurazione guidata vHA ===");
            _io.WriteLine("Premi INVIO per accettare il valore tra parentesi quadre. Nessuna password viene chiesta qui: verranno chieste all'installazione.");
            _io.WriteLine("");

            Text("Cartella di installazione dei programmi", "installDirectory", () => _c.InstallDirectory, v => _c.InstallDirectory = v);

            _io.WriteLine("\n-- vCenter --");
            Text("Server vCenter (nome DNS o IP)", "vCenter.server", () => _c.VCenter.Server, v => _c.VCenter.Server = v);
            Text("Utente vCenter (DOMINIO\\utente, vuoto = account del servizio)", "vCenter.user", () => _c.VCenter.User, v => _c.VCenter.User = v, allowEmpty: true);
            _c.VCenter.AllowUntrustedCertificate = YesNo("Accettare certificati vCenter non attendibili? (sconsigliato)", _c.VCenter.AllowUntrustedCertificate);

            _io.WriteLine("\n-- Account --");
            Text("Account del servizio Windows (consigliato gMSA: DOMINIO\\nome$)", "serviceAccount", () => _c.ServiceAccount, v => _c.ServiceAccount = v);
            Text("Account del task schedulato CSU (consigliato gMSA)", "taskAccount", () => _c.TaskAccount, v => _c.TaskAccount = v);

            _io.WriteLine("\n-- Nodi --");
            Nodes();

            _io.WriteLine("\n-- Cluster --");
            Text("Nome del cluster (max 15 caratteri)", "cluster.name", () => _c.Cluster.Name, v => _c.Cluster.Name = v);
            Text("Indirizzo IP del cluster", "cluster.ipAddress", () => _c.Cluster.IpAddress, v => _c.Cluster.IpAddress = v);
            var witness = Choice("Testimone del quorum (obbligatorio con numero pari di nodi)", new[] { "FileShare", "None" }, _c.Nodes.Count % 2 == 0 ? "FileShare" : _c.Cluster.Witness.ToString());
            _c.Cluster.Witness = (WitnessType)Enum.Parse(typeof(WitnessType), witness);
            if (_c.Cluster.Witness == WitnessType.FileShare)
                Text("Percorso UNC della share testimone (\\\\server\\share\\cartella)", "cluster.witnessPath", () => _c.Cluster.WitnessPath, v => _c.Cluster.WitnessPath = v);
            else _c.Cluster.WitnessPath = "";
            if (YesNo("Impostare soglia di failover del ruolo (default Windows se no)?", _c.Cluster.FailoverThreshold > 0))
            {
                Int("Numero massimo di errori", "cluster.failoverThreshold", () => _c.Cluster.FailoverThreshold, v => _c.Cluster.FailoverThreshold = v);
                Int("... nell'arco di quante ore", "cluster.failoverPeriodHours", () => _c.Cluster.FailoverPeriodHours, v => _c.Cluster.FailoverPeriodHours = v);
            }
            else { _c.Cluster.FailoverThreshold = 0; _c.Cluster.FailoverPeriodHours = 0; }

            _io.WriteLine("\n-- Availability group --");
            AvailabilityGroups();

            return Finish();
        }

        private void Nodes()
        {
            if (_c.Nodes.Count == 0) _c.Nodes.Add(new NodeSettings());
            int i = 0;
            while (true)
            {
                if (i >= _c.Nodes.Count) _c.Nodes.Add(new NodeSettings());
                var n = _c.Nodes[i]; var p = "nodes[" + i + "]";
                if (i >= 2)
                {
                    if (!YesNo("Aggiungere un altro nodo oltre ai " + i + " già inseriti?", false)) { _c.Nodes.RemoveRange(i, _c.Nodes.Count - i); break; }
                    _c.Nodes[i] = n = new NodeSettings();
                }
                Text("Nome della VM del nodo " + (i + 1) + " in vCenter", p + ".vmName", () => n.VmName, v => n.VmName = v);
                Text("Nome DNS del nodo " + (i + 1), p + ".dnsName", () => n.DnsName, v => n.DnsName = v);
                i++;
                if (i >= 8) break;
            }
        }

        private void AvailabilityGroups()
        {
            int i = 0;
            while (true)
            {
                if (i >= _c.AvailabilityGroups.Count) _c.AvailabilityGroups.Add(new AgSettings());
                var a = _c.AvailabilityGroups[i]; var p = "availabilityGroups[" + i + "]";
                _io.WriteLine("\nAvailability group " + (i + 1));
                Text("Nome (es. isql2025a-AG01)", p + ".name", () => a.Name, v => a.Name = v);
                Text("Nome istanza SQL (vuoto = istanza predefinita)", p + ".instanceName", () => a.InstanceName, v => a.InstanceName = v, allowEmpty: true);
                Int("Porta TCP dell'istanza (0 = usa SQL Browser)", p + ".sqlPort", () => a.SqlPort, v => a.SqlPort = v);
                Text("Cartella radice dei mount point (es. M:\\" + (string.IsNullOrEmpty(a.Name) ? "isql2025a-AG01" : a.Name) + ")", p + ".mountRoot", () => a.MountRoot, v => a.MountRoot = v);
                if (string.IsNullOrEmpty(a.DiskNameFilter) && !string.IsNullOrEmpty(a.Name)) a.DiskNameFilter = "*" + a.Name + "*";
                Text("Filtro sul nome dei VMDK dei dischi dell'AG (jolly * e ?)", p + ".diskNameFilter", () => a.DiskNameFilter, v => a.DiskNameFilter = v);
                if (string.IsNullOrEmpty(a.InitialOwner)) a.InitialOwner = _c.Nodes[0].VmName;
                Choice2("VM che possiede i dischi adesso (proprietario iniziale)", p + ".initialOwner", _c.Nodes.Select(n => n.VmName).ToArray(), () => a.InitialOwner, v => a.InitialOwner = v);
                Text("Nome di rete del ruolo cluster / listener (max 15 caratteri)", p + ".listenerName", () => a.ListenerName, v => a.ListenerName = v);
                Text("Indirizzo IP del listener", p + ".listenerIp", () => a.ListenerIp, v => a.ListenerIp = v);
                a.ReservationEnabled = YesNo("Spostare le riserve CPU/memoria insieme ai dischi?", a.ReservationEnabled);
                if (a.ReservationEnabled)
                {
                    Int("Riserva CPU in MHz", p + ".reservationCpuMHz", () => a.ReservationCpuMHz, v => a.ReservationCpuMHz = v);
                    Int("Riserva memoria in MB", p + ".reservationMemMB", () => a.ReservationMemMB, v => a.ReservationMemMB = v);
                }
                else { a.ReservationCpuMHz = 0; a.ReservationMemMB = 0; }
                Choice2("Se il nodo proprietario è acceso ma SQL non risponde: Force (stacca comunque i dischi) o Abort", p + ".ownerUnreachablePolicy", new[] { "Force", "Abort" }, () => a.OwnerUnreachablePolicy.ToString(), v => a.OwnerUnreachablePolicy = (OwnerPolicy)Enum.Parse(typeof(OwnerPolicy), v));
                if (YesNo("Modificare le impostazioni avanzate (tempi, log, opzioni SQL, database Dummy, nome task)?", false))
                {
                    Int("Secondi di attesa del CSU prima di fermarlo", p + ".csuWaitSeconds", () => a.CsuWaitSeconds, v => a.CsuWaitSeconds = v);
                    Int("Durata massima dell'avvio del servizio in secondi", p + ".failoverTimeoutSeconds", () => a.FailoverTimeoutSeconds, v => a.FailoverTimeoutSeconds = v);
                    Int("Livello di log del CSU (1 errori, 2 info, 3 debug)", p + ".csuLogLevel", () => a.CsuLogLevel, v => a.CsuLogLevel = v);
                    Int("Intervallo del CSU in secondi (multiplo di 60)", p + ".csuIntervalSeconds", () => a.CsuIntervalSeconds, v => a.CsuIntervalSeconds = v);
                    Text("Opzioni di connessione SQL", p + ".sqlOptions", () => a.SqlOptions, v => a.SqlOptions = v, allowEmpty: true);
                    a.CreateDummyDatabase = YesNo("Creare il database Dummy se manca?", a.CreateDummyDatabase);
                    if (a.CreateDummyDatabase) Text("Nome del database Dummy", p + ".dummyDatabaseName", () => a.DummyDatabaseName, v => a.DummyDatabaseName = v);
                    Text("Nome del task schedulato (vuoto = automatico)", p + ".taskName", () => a.TaskName, v => a.TaskName = v, allowEmpty: true);
                }
                i++;
                if (!YesNo("Aggiungere un altro availability group?", i < _c.AvailabilityGroups.Count)) { _c.AvailabilityGroups.RemoveRange(i, _c.AvailabilityGroups.Count - i); break; }
            }
        }

        private DeploymentConfig Finish()
        {
            var v = DeploymentValidator.Validate(_c);
            _io.WriteLine("\n=== Controllo finale ===");
            foreach (var issue in v.Issues) _io.WriteLine(issue.ToString());
            if (v.IsValid) _io.WriteLine(v.Warnings.Any() ? "Configurazione valida con avvisi." : "Configurazione valida.");
            else throw new InvalidOperationException("The configuration is not valid: " + string.Join("; ", v.Errors.Select(e => e.Path + ": " + e.Message)));
            return _c;
        }

        // ------------------------------------------------------------ prompts

        private string Read()
        {
            var s = _io.ReadLine();
            if (s == null) throw new OperationCanceledException("Input terminated before the end of the wizard");
            return s.Trim();
        }

        private void Text(string prompt, string path, Func<string> get, Action<string> set, bool allowEmpty = false)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var cur = get() ?? "";
                _io.WriteLine(prompt + (cur.Length > 0 ? " [" + cur + "]" : "") + ":");
                var a = Read();
                if (a.Length == 0) a = cur;
                if (a == "-" ) a = "";
                if (a.Length == 0 && !allowEmpty) { _io.WriteLine("  valore obbligatorio"); continue; }
                set(a);
                if (Accept(path)) return;
            }
            throw new InvalidOperationException("Too many invalid answers for " + path);
        }

        private void Int(string prompt, string path, Func<int> get, Action<int> set)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                _io.WriteLine(prompt + " [" + get().ToString(CultureInfo.InvariantCulture) + "]:");
                var a = Read();
                if (a.Length == 0) { if (Accept(path)) return; continue; }
                if (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) { _io.WriteLine("  inserisci un numero intero non negativo"); continue; }
                set(n);
                if (Accept(path)) return;
            }
            throw new InvalidOperationException("Too many invalid answers for " + path);
        }

        private void Choice2(string prompt, string path, string[] options, Func<string> get, Action<string> set)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var v = Choice(prompt, options, get());
                set(v);
                if (Accept(path)) return;
            }
            throw new InvalidOperationException("Too many invalid answers for " + path);
        }

        private string Choice(string prompt, string[] options, string def)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                _io.WriteLine(prompt + " (" + string.Join("/", options) + ") [" + def + "]:");
                var a = Read();
                if (a.Length == 0) a = def;
                var hit = options.FirstOrDefault(o => string.Equals(o, a, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
                _io.WriteLine("  scegli uno tra: " + string.Join(", ", options));
            }
            throw new InvalidOperationException("Too many invalid answers");
        }

        private bool YesNo(string prompt, bool def)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                _io.WriteLine(prompt + " (s/n) [" + (def ? "s" : "n") + "]:");
                var a = Read().ToLowerInvariant();
                if (a.Length == 0) return def;
                if (a == "s" || a == "si" || a == "sì" || a == "y" || a == "yes") return true;
                if (a == "n" || a == "no") return false;
                _io.WriteLine("  rispondi s oppure n");
            }
            throw new InvalidOperationException("Too many invalid answers");
        }

        /// <summary>True when the field has no validation error; shows its warnings.</summary>
        private bool Accept(string path)
        {
            var v = DeploymentValidator.Validate(_c);
            var errors = v.Errors.Where(e => e.Path == path).ToList();
            foreach (var e in errors) _io.WriteLine("  ERRORE: " + e.Message);
            foreach (var w in v.Warnings.Where(w => w.Path == path)) _io.WriteLine("  attenzione: " + w.Message);
            return errors.Count == 0;
        }
    }
}
