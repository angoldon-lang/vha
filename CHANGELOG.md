# Changelog

Versioni a calendario `AAAA.MM.N` (N riparte da 1 ogni mese). Procedura di rilascio: [docs/RELEASE.md](docs/RELEASE.md).
Il numero è definito solo in `Directory.Build.props`; la voce in cima a questo file deve coincidere (verificato dai test e da `Build-Package.ps1`).

## [2026.10.1] - 2026-10-14

Prima versione con numerazione a calendario. Sostituisce la serie di sviluppo "3.0" (ricostruzione completa di vHA/Jelena 2.0.0).

### Novità
- **Avvio locale senza vCenter** (`[Failover] LocalStartWithoutVCenter=Y`, predefinito Y): se vCenter non risponde ma TUTTI i dischi del gruppo sono già visibili a questa VM, il servizio mette online i database senza vCenter (riavvio del servizio, riavvio della VM, restart di vSphere HA). Se manca anche un solo disco, il failover viene rifiutato senza modificare nulla. Gli spostamenti di dischi tra nodi richiedono sempre vCenter.
- Documentazione: requisiti di licenza e matrice di compatibilità (`docs/LICENZE-E-COMPATIBILITA.md`), gestione vCenter (`docs/VCENTER.md`).
- Numero di versione stampato nei log (servizio e CSU), in `--version` di tutti i programmi, in `--check` e nel file `VERSION.txt` del pacchetto.

### Incluso (rispetto a vHA 2.0.0)
- Riscrittura in C# (Vha.Core / Vha.Infrastructure / ClusteredService / CsuCmd / VhaSetup) con correzione dei difetti critici 1-6 documentati in `docs/ANALISI.md`.
- Compatibilità SQL Server 2025 e vSphere 8 (PowerCLI), .NET Framework 4.8.
- Setup guidato e configuratore, installazione dei prerequisiti, pacchetto `VhaSetup-<versione>.zip`.
- Oltre 500 test automatici (unit, fault injection, SQL Server 2025 reale, PowerShell reale con stub, crash/freeze/soak); vedi `docs/VERIFICA.md` per ciò che è verificato e per i rischi residui (mai provato su Windows/vCenter/cluster reali).
