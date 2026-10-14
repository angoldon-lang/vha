# vHA (Jelena) 3.0 – alta affidabilità dei database SQL Server su VMware

Rifacimento controllato di vHA 2.0.0 (VB.NET, solo binari) in C#, con i difetti di affidabilità corretti e una suite di test automatici.
Il meccanismo è quello originale: i dischi (VMDK) di dati e log di un availability group vengono staccati dalla VM che li usa e
agganciati alla VM che diventa attiva; un servizio in cluster (WSFC) fa il lavoro, un task schedulato (CSU) tiene allineata la configurazione.

> **Stato.** Compila e supera 512 test automatici, tra cui test d'integrazione contro **SQL Server 2025 reale** e **PowerShell reale**, iniezione di guasti sul failover, nodi congelati, crash, prova di durata (20 failover con scritture continue) e test a proprietà su migliaia di stringhe ostili.
> **Non è mai stato eseguito su Windows, vCenter e cluster reali**: prima della produzione servono le prove del capitolo "Collaudo" in `docs/INSTALLAZIONE.md` (elenco dei rischi residui in `docs/VERIFICA.md`).
> Il codice è stato ricostruito dalla decompilazione di `ClusteredService.exe`/`CsuCmd.exe` 2.0.0: vedi `docs/ANALISI.md` per cosa è cambiato e perché.

## Struttura
| Percorso | Contenuto |
|---|---|
| `src/Vha.Core` | Logica pura (netstandard2.0), interamente testabile: failover, CSU, policy di sicurezza, SQL/PowerCLI, **modello di configurazione, validatore, wizard, piano di installazione** |
| `src/Vha.Infrastructure` | Accesso a SQL Server, PowerCLI, Task Scheduler, EventLog, DPAPI, **esecutore del setup** (net48) |
| `src/ClusteredService` | `ClusteredService.exe`: servizio Windows del cluster (+ `--check`) |
| `src/CsuCmd` | `CsuCmd.exe`: aggiornamento continuo (task schedulato) |
| `src/VhaSetup` | `VhaSetup.exe`: configuratore e installatore (wizard, validate, plan, install, cluster, check, uninstall) |
| `tests/Vha.Core.Tests` | test di logica e **iniezione di guasti** |
| `tests/Vha.Infrastructure.Tests` | test d'integrazione: SQL Server 2025 reale e PowerShell reale |
| `installer/` | `Build-Package.ps1`, `Setup.cmd`, downloader dei prerequisiti |
| `sql/`, `scripts/`, `examples/`, `docs/` | script del database, utilità, esempio di configurazione, documentazione |

## Installare (riassunto)
```
pwsh -File installer\Build-Package.ps1 -IncludePrerequisites      # crea dist\VhaSetup-3.0.0.zip
# sui nodi:  Setup.cmd -> 1 wizard -> 3 installa   (poi, a tutti i nodi installati: 4 cluster, 5 verifica)
```
Dettagli: `docs/INSTALLAZIONE.md`. Cosa è provato e cosa no: `docs/VERIFICA.md`.

## Compilare e collaudare
```
dotnet build vha.sln -c Release
dotnet test tests/Vha.Core.Tests                 # nessun requisito
tests/integration/start-lab.sh && dotnet test tests/Vha.Infrastructure.Tests    # richiede Docker (SQL Server 2025)
```

## Requisiti sui nodi
Windows Server 2016-2025 (il setup installa .NET Framework 4.8, Failover Clustering e il modulo `VMware.PowerCLI` 12/13),
`disk.EnableUUID=TRUE` sulle VM. Il vecchio snap-in PowerCLI non e' supportato.
