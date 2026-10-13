# Installazione, configurazione e collaudo (vHA 3.0)

Rispetto alla guida 2.0.0 restano invariati: layout dei dischi con mount point, `disksign.ini`, SQL con la stessa istanza nominata su ogni nodo,
cluster WSFC senza storage condiviso con witness, nome servizio `Jelena-<AG>`, cartella `C:\vHA\Bin\<istanza>\<AG>\`.
Cambia quanto segue.

## 1. Prerequisiti
* Windows Server 2016–2025, .NET Framework 4.8, funzionalità Failover Clustering, `Enable-PSRemoting -Force`.
* `Install-Module VMware.PowerCLI -Scope AllUsers` (12/13). **Non** serve `Add-PSSnapin`, né la procedura "Disable advanced internet properties".
* Su ogni VM: `disk.EnableUUID = TRUE` (e riavvio). Dischi di dati/log solo su controller **SCSI** (chiavi 2000–2063).
* Certificato vCenter valido (non usare `-InvalidCertificateAction Ignore` in produzione).
* Account di dominio per servizio e task: preferire **gMSA** per il task (nessuna password da far scadere).

## 2. Installazione dei file
Copiare in `C:\vHA\Bin\<istanza>\<AG>\`: `ClusteredService.exe`, `Vha.Core.dll`, `Vha.Infrastructure.dll`, `TaskScheduler.dll`, `jelena.ini`;
in `C:\vHA\Bin\`: `CsuCmd.exe` con le stesse dll. Servizio: `installutil /AGName=<AG> ...\ClusteredService.exe` (richiede l'utente di servizio).
`jelena.ini` come prima; chiavi nuove, tutte opzionali:
```
[AG]
SqlOptions=Encrypt=True;TrustServerCertificate=True     ; opzioni aggiunte alla connessione al DB Jelena locale
[Failover]
OwnerUnreachablePolicy=Force   ; Force (default) | Abort : cosa fare se la VM proprietaria è accesa ma SQL non risponde
CsuWaitSeconds=120             ; attesa del CSU prima di fermarlo
FailoverTimeoutSeconds=900     ; durata massima dell'avvio del servizio
[General]
LogRetentionDays=31
```
Log: `<cartella servizio>\Jelena-<AG>-Log\yyyyMMdd.log`, `Csu-<AG>-Log\` per il CSU, più Event Viewer (sorgente `Jelena Console Update - <AG>`).

## 3. Database Jelena
1. Creare/ripristinare `Jelena` su ogni nodo (locale, non sui dischi condivisi). `sql/01_schema.sql` crea le tabelle mancanti.
2. `sql/02_register_availability_group.sql` su ogni nodo (AG, nodi, `is_local` corretto anche con istanze nominate).
3. `scripts/Get-VhaDiskInfo.ps1` produce gli `INSERT` di `JDisks`.
4. `sql/03_register_databases.sql` sul nodo attivo (registra anche `Dummy01`, rifiuta DB con file fuori dai dischi dell'AG, incrementa l'SCN).
5. `JSettings.ConnectVCenter`: `Connect-VIServer vc01.corp.local -User 'DOM\svcVha' -Password dpapi:<valore> -Protocol https`.
   Generare `dpapi:` **su ciascun nodo**: `CsuCmd.exe --protect` (la protezione è per macchina).
6. Task schedulato del CSU: `CsuCmd.exe <VM> <AG> <istanza> 2`, ogni minuto, "non avviare una nuova istanza" (c'è comunque un mutex), utente = gMSA/utente di dominio con "Log on as a batch job".
7. `sql/05_health_check.sql` deve restituire **zero righe**.

## 4. Verifica prima di ogni uso: `ClusteredService.exe --check Jelena-<AG>`
Controlla ini, database Jelena, task, connessione vCenter, dischi di ogni nodo (UUID presenti, chi li ha attaccati, snapshot) e remoting verso gli altri nodi.
Non sposta nulla. Esce con codice 1 se qualcosa non va (adatto al monitoraggio).

## 5. Collaudo obbligatorio (mai eseguito sul codice 3.0)
Su un ambiente di prova identico alla produzione (2 VM, vCenter, SQL, cluster), con DB di prova di più file/filegroup:
1. `--check` pulito. Soft test: servizio offline/online sul proprietario ⇒ nessuna interruzione.
2. Hard test: spostamento del ruolo ⇒ DB online sul secondo nodo, `JDisks`/`SCN`/`CurrentOwner` coerenti, CSU riabilitato.
3. Failover con **VM proprietaria spenta** e con **VM accesa ma servizio SQL fermo** (`Force` e `Abort`).
4. **Crash a metà**: interrompere il servizio dopo lo stacco dei dischi; il riavvio deve ricomporre lo stato.
5. VM con snapshot ⇒ failback automatico.
6. Nodo remoto spento durante il CSU ⇒ il CSU continua; DB creato mentre l'altro nodo è giù ⇒ allineamento al ritorno.
7. Perdere volutamente il solo `.ldf` di un DB gestito ⇒ **nessun** `DROP DATABASE`.
8. DB con più file e filegroup, DB con nome con apici/parentesi, due AG sulla stessa istanza.
9. Patch SQL a rotazione (passivo → failover → attivo) e cluster con vSphere HA/DRS attivi (regola di anti-affinità).
10. Prove di ripristino dei backup (la sola protezione da corruzione dei dati).

## 6. Migrazione verso SQL Server 2025 / vSphere 8
* Meccanismo (detach/attach, `CREATE DATABASE … FOR ATTACH`) indipendente dalla versione, ma **stessa build su tutti i nodi**: un DB agganciato a una build più recente non torna su una più vecchia. Aggiornare prima il passivo, poi failover, poi l'attivo.
* TLS: l'app usa il default del sistema (TLS 1.2/1.3); per SQL con cifratura obbligatoria usare `[AG] SqlOptions` e `Encrypt` nelle stringhe di `JNodes` (lo script 02 usa `Encrypt=True;TrustServerCertificate=True`).
* PowerCLI 13 + vSphere 8: solo https, modulo (non snap-in), controller SCSI (PVSCSI consigliato). NVMe/SATA non supportati dal prodotto.
* Licenza SQL Server: vedere `ANALISI.md` §C.
