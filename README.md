# vHA (Jelena) 3.0 – alta affidabilità dei database SQL Server su VMware

Rifacimento controllato di vHA 2.0.0 (VB.NET, solo binari) in C#, con i difetti di affidabilità corretti e una suite di test automatici.
Il meccanismo è quello originale: i dischi (VMDK) di dati e log di un availability group vengono staccati dalla VM che li usa e
agganciati alla VM che diventa attiva; un servizio in cluster (WSFC) fa il lavoro, un task schedulato (CSU) tiene allineata la configurazione.

> **Stato.** Compila e supera tutti i test automatici sulla logica (167 al momento della consegna). **Non è mai stato eseguito contro un vCenter, un SQL Server e un
> Windows reali**: prima della produzione servono le prove del capitolo "Collaudo" in `docs/INSTALLAZIONE.md`.
> Il codice è stato ricostruito dalla decompilazione di `ClusteredService.exe`/`CsuCmd.exe` 2.0.0: vedi `docs/ANALISI.md` per cosa è cambiato e perché.

## Struttura
| Percorso | Contenuto |
|---|---|
| `src/Vha.Core` | Logica pura (netstandard2.0), interamente testabile: coordinatore del failover, coordinatore del CSU, generazione di SQL/PowerCLI, policy di sicurezza |
| `src/Vha.Infrastructure` | Accesso a SQL Server, PowerCLI, Task Scheduler, EventLog, DPAPI (net48) |
| `src/ClusteredService` | `ClusteredService.exe`: servizio Windows del cluster (+ `--check` diagnostico) |
| `src/CsuCmd` | `CsuCmd.exe`: aggiornamento continuo, eseguito dal task schedulato |
| `tests/Vha.Core.Tests` | test xUnit con fake di vCenter/SQL/cluster |
| `sql/` | Schema, registrazione AG e database, generatore di attach, health check |
| `scripts/` | `Get-VhaDiskInfo.ps1` (popola `JDisks`) |

## Compilare e collaudare
```
dotnet build vha.sln -c Release          # produce ClusteredService.exe e CsuCmd.exe (net48, x64)
dotnet test tests/Vha.Core.Tests          # funziona anche su Linux/macOS
```
I binari da distribuire sono in `src/ClusteredService/bin/Release/net48/` e `src/CsuCmd/bin/Release/net48/`
(più `Vha.Core.dll`, `Vha.Infrastructure.dll`, `TaskScheduler.dll`). `diskext64.exe` **non serve più**.

## Requisiti sui nodi
Windows Server 2016–2025, .NET Framework 4.8, Failover Clustering, PowerShell 5.1 con PSRemoting abilitato,
modulo `VMware.PowerCLI` 12/13 per tutti gli utenti (`Install-Module VMware.PowerCLI -Scope AllUsers`), `disk.EnableUUID=TRUE` sulle VM.
Il vecchio snap-in PowerCLI non è più supportato.
