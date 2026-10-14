# Verifica del software

Obiettivo: nessun errore in produzione. Nessuna suite di test lo garantisce; qui è scritto **cosa è stato provato, come, e cosa NO**.

## Cosa è stato eseguito
| Livello | Come | Prova |
|---|---|---|
| Logica (failover, CSU, policy, setup) | `tests/Vha.Core.Tests`, 321 test xUnit con fake di vCenter, SQL, cluster, task | scenari, ordine delle operazioni, validazione, generazione di ini/XML/script |
| **Iniezione di guasti** | stessi test: 6 situazioni di partenza × **ogni** operazione esterna del failover × (guasto prima / dopo l'effetto) | invarianti: mai un disco su due VM; DB rilasciati prima dello stacco; marcatore prima dello stacco; CSU sempre riabilitato; **un secondo avvio converge sempre** |
| Mutation test manuali | 17 modifiche dannose reintrodotte a mano nel codice (CSU mai riabilitato, niente rilascio DB, niente marcatore, drop senza controllo dei file, mapping disco per sottostringa, escape SQL assente, validazioni tolte…) | tutte intercettate; una (rollback di un attach parziale) ha richiesto un test in più |
| **SQL Server 2025 reale** (2 istanze Docker, cartella condivisa = "dischi") | `tests/Vha.Infrastructure.Tests`: gli script `sql/*.sql` eseguiti davvero, config store, connettore, attach/detach di DB con più filegroup e nomi con `'` e `]`, CSU (replica, peer spento, drop dei soli fantasmi), failover completo A→B→A, setup del database su nodo attivo e passivo, idempotenza | dati integri (1000 righe), file tutti presenti, SCN/owner coerenti |
| **Crash reale** | `docker kill` (SIGKILL) del nodo con transazione aperta e pagine sporche; il nodo B attacca i file | solo i dati committati, recovery riuscita |
| **PowerShell reale** (Microsoft.PowerShell.SDK) con cmdlet VMware/Storage simulati | host (errori, timeout, variabili), connessione vCenter con password piena di metacaratteri, query VM/dischi/datastore, spec `ReconfigVM` (chiavi, controller, unit, riserve, nessun `fileOperation`), dischi guest on/offline, inventario CSU (join per UUID, `disksign.ini`) | valori ricevuti esatti, iniezioni inerti |
| Sintassi di **tutti** gli script generati | parser PowerShell su ~170 script, anche con nomi/percorsi ostili (`'`, `` ` ``, `$()`, virgolette) | zero errori |
| Binari reali su **Mono** | `VhaSetup.exe` dal pacchetto: `validate`, `plan`, `wizard` con stdin, `install --dry-run`, `cluster`, errori d'uso; `CsuCmd.exe` uso | output e codici di uscita corretti |
| Pacchetto | `Build-Package.ps1`: build, test, controllo file obbligatori, assenza di assembly di riferimento, SHA256 | produce lo zip |

| **Stringhe ostili, a proprietà** | migliaia di stringhe casuali (apici di ogni tipo, `$()`, backtick, a-capo, controlli, Unicode, coppie surrogate) fatte passare dai generatori e **rilette dal consumatore reale**: `PsQuote` in PowerShell, `SqlText`/template in SQL Server, `ProcessArgs` col parser degli argomenti Windows, ini, XML del task | nessuna differenza: i valori arrivano identici e nessun valore esce dalle virgolette |
| Fuzzing dei parser | 6000 file di configurazione mutati, 6000 testi `ConnectVCenter`, risposte casuali al wizard, dischi casuali | solo le eccezioni previste (`FormatException`, ...), mai un crash |
| **Nodi congelati** (`docker pause`) | connessione, rilascio dei database dopo la connessione, probe e copia del CSU verso un peer che si blocca | ogni operazione rinuncia in tempo limitato; la transazione è annullata; la configurazione locale resta intatta; dopo lo scongelamento il CSU riprende |
| **Avvio con SQL in partenza** | container fermato e riavviato durante il tentativo di connessione del servizio | il servizio attende e prosegue; un errore di login non viene ritentato |
| **Durata** | 20 failover consecutivi A↔B con un client che inserisce righe di continuo | 604 righe confermate = 604 presenti, nessun duplicato, `DBCC CHECKDB` pulito |

Riesecuzione: `tests/integration/start-lab.sh` (Docker) poi `dotnet test vha.sln`. Senza i container i test SQL sono **saltati**, non falliti; la CI li avvia.

## Difetti trovati dalla verifica (e corretti)
1. `sql/04`: `QUOTED_IDENTIFIER` e doppia parentesi `((FILENAME …` nell'`ATTACH` generato (non eseguiti prima).
2. Connettore: i numeri di `SqlException` non sono affidabili (.NET Core riporta 0); ora login/database/TLS **interrompono** il failover invece di sembrare un nodo spento.
3. Coordinatore: se la lettura di verifica dopo l'attach falliva, tentava un rollback di dischi già agganciati; ora si guarda la realtà e, se lo stato è ignoto, non si fa rollback.
4. `sql/03`: confronto con `LIKE` (i caratteri `[ % _` del percorso erano jolly) → confronto esatto.
5. Pacchetto: `System.Management.Automation.dll` di riferimento finiva accanto agli eseguibili; nome corretto di `Microsoft.Win32.TaskScheduler.dll`.
6. Nomi JSON scritti in PascalCase invece di camelCase.
7. `Instance` ora accetta una stringa di connessione completa (porte personalizzate, listener).
8. Nota di SQL Server (non di vHA): un nodo ucciso pochi secondi dopo un `CREATE DATABASE` con file secondari e prima del primo checkpoint non permette il redo (riprodotto con `sqlcmd` puro). Database in esercizio: nessun problema. Dopo aver creato un DB nuovo eseguire `CHECKPOINT`.

### Secondo giro di verifica
9. T-SQL **elimina** la sequenza `\` + a-capo dentro un literal (continuazione di riga): un valore così sarebbe cambiato in silenzio. I literal non emettono più a-capo grezzi (`NCHAR(13)/(10)`), i nomi di file li rifiutano, il template SQL rifiuta i valori con a-capo.
10. Il parser di `ConnectVCenter` ora tratta le virgolette tipografiche come PowerShell (prima raddoppiava gli apici ‘ ’).
11. Il validatore lasciava passare caratteri di controllo nei nomi (che poi rompevano l'XML del task): ora sono rifiutati in **ogni** campo.
12. `JDBFiles/JDBFileGroups.DBName` era `VARCHAR(100)` (come in 2.0.0): un database con nome non latino non si ritrovava più al failover. `01_schema.sql` ora allarga la colonna in place conservando le righe, e l'health check segnala le colonne ancora `VARCHAR`.
13. Un'istanza **congelata** produce "Connection Timeout Expired … pre-login handshake": era scambiata per un errore di certificato e annullava il failover. Il timeout ha ora la precedenza.
14. Una connessione **resettata durante l'avvio di SQL** era scambiata per un errore TLS: ora è transitoria; solo certificati/cifrari/autenticazione bloccano.
15. Se il proprietario risponde alla connessione ma si pianta durante il rilascio dei database, o se WinRM fallisce nello spegnere i dischi guest, il failover veniva annullato anche con `OwnerUnreachablePolicy=Force`: ora seguono la policy (`ReleaseTimeoutSeconds`, default 300 s).
16. Il servizio falliva subito se l'istanza SQL locale stava ancora partendo (avvio dei nodi dopo un blackout): ora ritenta (fino a ~2 minuti) solo gli errori transitori; il collegamento a vCenter ritenta 3 volte.
17. `ClusteredService.exe --check` verifica anche che `UniqueId` in `JDisks` coincida con l'UUID del disco in vCenter.

Verificato e **non** problematico: un database lasciato OFFLINE su un nodo (come i database pubblicati in replica) mentre l'altro nodo ne modifica i file riesce a tornare online (SQL Server rilegge i file): 5000 righe e il nuovo file presenti.

## Cosa NON è stato provato (rischio residuo)
* **Nessuna esecuzione su Windows**: servizio (`sc.exe`, gMSA), task (`Register-ScheduledTask -Xml`, gMSA), `secedit`, `Install-WindowsFeature`, `Enable-PSRemoting`, EventLog, DPAPI, WinRM tra nodi, `takeown/icacls`.
* **vCenter/PowerCLI reali**: le cmdlet sono simulate; la forma di `Get-HardDisk`, `ExtensionData.Backing.Uuid`, `ReconfigVM` va confermata su vSphere 7/8.
* **Cluster Windows** (`New-Cluster`, `Add-ClusterGenericServiceRole`, quorum, comportamento dei criteri di failover del ruolo).
* L'ipotesi **numero di serie Windows = UUID VMware senza trattini**.
* Mono non è .NET Framework: esegue il codice ma non riproduce binding, GAC e PowerShell 5.1.
* Prestazioni e durata del failover con molti database/dischi; storage-specific (vSAN, vVols).
Per questi punti serve il collaudo su un ambiente Windows/vSphere di prova (`INSTALLAZIONE.md` §6).

Totale al momento della consegna: **512 test** (321 di logica, 191 d'integrazione con SQL Server 2025 e PowerShell reali), tutti verdi.
