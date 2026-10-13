# Analisi di vHA 2.0.0 e correzioni della 3.0

Fonte: `ClusteredService.exe`, `CsuCmd.exe`, `cSQL.dll`, `cVMware.dll` (decompilati), `Jelena.bak`, la guida di installazione e gli script SQL.
Ogni riga indica la correzione e il test che la protegge (`tests/Vha.Core.Tests`). "Non verificabile qui" = codice nuovo di accesso a SQL/vCenter/Windows,
compilato ma mai eseguito contro sistemi reali.

## A. Difetti critici (rischio di perdita dati o di mancato failover)
| # | Difetto in 2.0.0 | Correzione 3.0 | Test |
|---|---|---|---|
| 1 | `CheckRecoveryPendingDB` (CSU, ogni minuto, su ogni nodo) eseguiva `DROP DATABASE` su **qualunque** DB OFFLINE/RECOVERY_PENDING con **almeno un** file mancante. Perso solo l'`.ldf`, il DROP cancellava l'`.mdf` | `GhostDropPolicy`: solo DB gestiti dall'AG, stato OFFLINE/RECOVERY_PENDING, **tutti** i file assenti e nodo che **non** possiede i dischi. Mai se un file sopravvive; mai DB di sistema/Jelena/altri | `GhostDropPolicyTests`, `CsuCoordinatorTests.The_owner_never_drops…`, `…just_detected_the_disks…` |
| 2 | `IsTaskRunning(…, bool bolIsRunning, …)` per valore: l'attesa del CSU non funzionava mai, failover e CSU in concorrenza | `ICsuTask`: attesa reale, stop dopo `CsuWaitSeconds`, riabilitazione sempre (anche se il failover fallisce); in più mutex per AG nel CSU | `Running_csu_task_is_waited_for`, `Stuck_csu_task…`, `Csu_task_is_re_enabled_even_when…` |
| 3 | Con proprietario non raggiungibile (`NOT-AVAILABLE`/`TIMEOUT`) i VMDK venivano staccati comunque, in silenzio | VM spenta ⇒ presa in carico sicura. VM accesa e SQL muto ⇒ `[Failover] OwnerUnreachablePolicy` = `Force` (default, comportamento originale ma con avviso) o `Abort`. Errori di login/configurazione non sono più confusi con "nodo giù" | `Owner_not_reachable_with_Force…`, `…Abort…`, `Powered_off_owner…` |
| 4 | I dischi venivano staccati **prima** di scrivere `##transitioning##`; se la scrittura falliva il servizio restava bloccato per sempre ("la lista dei dischi non corrisponde") | La posizione reale dei dischi si legge da vCenter (JAGroups.CurrentOwner è solo informativo). Il marcatore si scrive **prima** dello stacco e, se fallisce, non si tocca nulla. Stati misti/interrotti si ricompongono da soli al riavvio. Attach fallito ⇒ rollback ai proprietari precedenti | `Transitioning_marker_failure…`, `Crash_recovery_disks_attached_nowhere…`, `Mixed_state…`, `Failed_attach_rolls_back…`, `Disk_attached_to_two_nodes_is_refused` |
| 5 | `HD_List_Match` cercava `Id LIKE '%2050%'` nell'id intero: falso positivo con il moref della VM (`vm-2050`) | `DiskMatcher`: confronto esatto del DeviceKey; stesso slot con file diverso accettato solo se l'UUID conferma (Storage vMotion), altrimenti errore | `DiskMatcherTests`, `A_different_disk_using_the_same_key…`, `Moved_vmdk_file_is_followed…` |
| 6 | `WriteNewSCN` del servizio inghiottiva ogni eccezione | Errori di SCN/owner propagati; il failover non risulta riuscito se non è registrato | `Scn_write_failure_is_no_longer_swallowed` |

## B. Difetti alti
| Difetto | Correzione | Note |
|---|---|---|
| Mappatura SCSI→DeviceKey con costanti (`+3` se Windows ≥10, `diskext64.exe` parsato come testo, "la prima riga è C:") | Join per **UUID del disco** (`DiskIdentity.Join`): niente SCSIPort, niente `diskext64.exe` | `DiskIdentityTests`. Ipotesi da collaudare: serial Windows = UUID VMware senza trattini (richiede `disk.EnableUUID`) |
| `intControllerKey/intUnitNumber`: chiave 2031 → controller 0 / unit -1; 2046→unit 0; oltre 2060 non gestito; `device[]` con buchi `null` | `ScsiSlot` (2000+16·c+u, unit 7 riservata); array senza buchi; NVMe/SATA rifiutati con messaggio chiaro | `ScsiSlotTests`, `PowerCliScriptsTests` |
| Con un nodo spento il CSU abortiva (Open() senza try) e non aggiornava `JDisks` | Peer irraggiungibili saltati, il resto del ciclo prosegue | `ScnSyncTests`, `Peer_down_does_not_abort…` |
| SQL costruito per concatenazione (apici, `]`, NULL/date in `UpdateTableFromRemoteDatabase`) | Parametri ovunque; DDL con `QuoteName`/`Literal`; PowerShell con `PsQuote`; whitelist delle tabelle replicate | `SqlTextTests`, `AttachScriptBuilderTests`, `QuotingTests`, `TableSyncPlannerTests` |
| Un DB già presente ma OFFLINE non veniva mai riportato online (solo "RestoreACL") | `DbOnlinePlanner`: attach / `SET ONLINE` / niente / errore; un DB che fallisce non blocca gli altri; stato verificato dopo | `DbOnlinePlannerTests` |
| `RestoreACL_ICACLS` = `icacls <file>\* /q /c /t` (solo elenca), path senza virgolette, exit code ignorato | `takeown`/`icacls /reset` con quoting e controllo exit code | non verificabile qui |
| `ConnectVCenter` eseguito come script PowerShell (chi scrive in JSettings esegue codice da local admin); password in chiaro; `-Protocol http` | `VCenterConnectionSpec`: una sola cmdlet `Connect-VIServer`, parametri noti, valori passati come variabili, https forzato, password `dpapi:` (`CsuCmd.exe --protect`) | `VCenterConnectionSpecTests` |
| `RunScript`: flag TLS invertito (TLS 1.2 mai impostato), `finally { val.Dispose() }` con `val` nullo, errori accumulati in una stringa condivisa | TLS 1.2/1.3 abilitati, eccezioni vere, timeout per script, variabili separate | non verificabile qui |
| `OnStart` sincrono: oltre 30 s l'SCM dà errore 1053 nel mezzo del failover | `RequestAdditionalTime` periodico + timeout complessivo (`FailoverTimeoutSeconds`) | non verificabile qui |
| Log `<giorno>.log` in append infinito; se non scrivibile il servizio non parte | `yyyyMMdd.log` con retention; il log non solleva mai eccezioni | `FileLogTests` |
| `EventLog.SourceExists` testato con `IsNothing(bool)` (mai creato), `CsuCmd` stampava "servono 4 argomenti" sempre e usciva sempre con 0 | Sorgente creata; argomenti validati; exit code 1 su errore | — |
| `diskext64.exe` (Sysinternals) richiesto sui nodi | Eliminato | — |
| Riattivazione del task con `Definition + RegisterChanges` avrebbe azzerato la password memorizzata | `Task.Enabled` | — |

## C. Aperti / fuori ambito (da decidere)
Verifica eseguita e rischi residui: `VERIFICA.md`; installazione: `INSTALLAZIONE.md`.

* **Collaudo reale** (vCenter, SQL, Windows, cluster): obbligatorio, elenco in `INSTALLAZIONE.md`.
* **Schema `Jelena`**: ricostruito dal codice (`sql/01_schema.sql`); confrontarlo con un database funzionante.
* **Oggetti server** (login con stesso SID, job, linked server, certificati TDE): non gestiti dal prodotto. Consigliato dbatools (`Copy-DbaLogin`, `Sync-DbaAvailabilityGroup`-like) e un controllo di drift schedulato.
* **vCenter punto unico di guasto**: senza vCenter nessun failover. Mitigare con vCenter in HA/VCHA; più connection string è un'estensione futura.
* **Backup**: la guida usa `D:\` locale; usare una share condivisa, altrimenti la catena dei log si spezza fra i nodi.
* **FILESTREAM / In-Memory OLTP**: file e filegroup particolari non sono descritti da `JDBFiles` in modo completo; da validare prima dell'uso.
* **Password già pubblicate** (guida 2.0.0, `Jelena.bak`): ruotarle.
* **Licenze** SQL Server Standard: i diritti di failover passivo richiedono SA e un nodo davvero passivo; confermare con Microsoft/reseller.
