# Installazione di vHA con il setup guidato

Il setup sostituisce le ~57 pagine della guida 2.0.0: un file di configurazione (`vha-deployment.json`) descrive tutto, `VhaSetup.exe` lo valida,
mostra il piano e lo esegue su ogni nodo. Ogni passo è idempotente: se qualcosa si ferma (errore, riavvio richiesto) si corregge e si **ripete lo stesso comando**.

> **Stato.** Il setup è stato verificato con test automatici (vedi `VERIFICA.md`), ma le operazioni che richiedono Windows
> (servizio, task schedulato, funzionalità Windows, secedit, cluster, PowerCLI reale) **non sono mai state eseguite su un server Windows reale**.
> Prima della produzione eseguire la procedura su un ambiente di prova e le prove del capitolo "Collaudo".

## 0. Requisiti
| Elemento | Requisito |
|---|---|
| Nodi | Windows Server 2016–2025 (VM VMware), almeno 2, stesso sistema operativo |
| SQL Server | stessa edizione/versione/build e **stessa istanza nominata** su tutti i nodi (2016–2025); database di sistema e `Jelena` su dischi **locali** |
| vSphere | vCenter 6.7–8.0 raggiungibile in https (443) dai nodi; `disk.EnableUUID=TRUE` su tutte le VM; dischi dati/log su controller **SCSI** (PVSCSI consigliato) |
| Dischi dell'AG | un VMDK di dati e uno di log (o più) per AG, GPT, montati come **mount point** `…\Data` e `…\Log` sotto la `mountRoot` dello stesso AG, **nessuna lettera di unità** |
| Account | gMSA consigliati per servizio e task (`DOMINIO\nome$`); il servizio è amministratore locale e sysadmin delle istanze; il task ha "Log on as a batch job" (il setup assegna i diritti) |
| vCenter | utente con ruolo minimo sulle VM del cluster (aggiunta/rimozione dischi, riserve) e lettura su datacenter/datastore |
| Rete / AD | DNS dei nodi, cluster name object (CNO) pre-creato o diritto di creare computer nell'OU, share per il witness se i nodi sono pari |
| Antivirus | escludere `.mdf/.ndf/.ldf`, la cartella di installazione e le cartelle `…\Data`, `…\Log` |

Il setup installa da solo: Failover Clustering, PowerShell remoting, .NET Framework 4.8, modulo `VMware.PowerCLI` (offline se la cartella `prereq` è popolata).

## 1. Ottenere il pacchetto
```
pwsh -File installer\Build-Package.ps1 -IncludePrerequisites     # su un PC con Internet; produce dist\VhaSetup-<versione>.zip
```
Senza `-IncludePrerequisites` il pacchetto scarica PowerCLI da Internet sui server; con l'opzione include .NET 4.8 e PowerCLI (firme verificate)
per server isolati. La build esegue prima i test unitari e controlla la completezza del pacchetto. `SHA256SUMS.txt` permette di verificare la copia.

## 2. Configurare
```
Setup.cmd  ->  1   (wizard)         oppure         VhaSetup wizard --config vha-deployment.json
```
Il wizard valida ogni risposta con le stesse regole del setup (nomi, IP, percorsi, account, quorum, duplicati…) e propone valori predefiniti.
In alternativa partire da `examples/vha-deployment.example.json` (commentato). **Nessuna password è nel file.**
Copiare lo stesso file su tutti i nodi. `VhaSetup validate --config …` lo controlla in qualsiasi momento.

Impostazioni principali (tutte nel file, tutte con valore predefinito sensato):

| Campo | Significato |
|---|---|
| `vCenter.server/user` | vCenter e utente (vuoto = autenticazione pass-through con l'account del servizio). La password è chiesta all'installazione e salvata cifrata (DPAPI) nel `Jelena` locale |
| `serviceAccount`, `taskAccount` | account del servizio e del task CSU |
| `cluster.*` | nome, IP, witness (`FileShare`/`None`), soglia di failover opzionale |
| `nodes[]` | `vmName` (nome in vCenter) e `dnsName` |
| `availabilityGroups[]` | `name`, `instanceName`, `sqlPort`, `mountRoot`, `diskNameFilter`, `initialOwner`, `listenerName/Ip`, riserve CPU/memoria, `ownerUnreachablePolicy` (Force/Abort), tempi (`csuWaitSeconds`, `failoverTimeoutSeconds`, `releaseTimeoutSeconds`), `sqlOptions`, intervallo e log del CSU, database Dummy, nome del task |

## 3. Installare
Su **ogni nodo**, da amministratore:
```
Setup.cmd -> 2   (piano: mostra i passi)            VhaSetup plan    --config vha-deployment.json [--scripts]
Setup.cmd -> 3   (installa)                         VhaSetup install --config vha-deployment.json
```
`--dry-run` stampa gli script senza eseguirli. Il nodo è riconosciuto dal nome del computer (o `--node`). Codici di uscita: 0 ok, 1 passo fallito, 2 uso/configurazione errati, 3 riavvio necessario.

Passi eseguiti su ogni nodo, nell'ordine (ognuno controlla lo stato prima di agire):

| # | Passo | Dettaglio |
|---|---|---|
| 1 | Sistema | amministratore, PowerShell ≥ 5.1, Windows Server ≥ 2016 |
| 2 | .NET Framework 4.8 | già presente su WS2022/2025; altrimenti installer offline con firma Microsoft verificata; **può chiedere un riavvio** |
| 3 | Funzionalità | Failover Clustering, PSRemoting (può chiedere un riavvio) |
| 4 | PowerCLI | modulo ≥ 12 (offline dalla cartella `prereq\PowerCLI` o dalla Gallery); certificati vCenter **validati** salvo `allowUntrustedCertificate` |
| 5–6 | Account | membro degli Administrators locali; diritti "Log on as a service" e "Log on as a batch job" (secedit) |
| 7 | File | copia con verifica SHA-256 in `<install>\<istanza>\<AG>\` (servizio) e `<install>\` (CsuCmd); **rifiuta di sovrascrivere un servizio in esecuzione** |
| 8 | `jelena.ini` | generato dal file di configurazione (backup se cambia) |
| 9 | Servizio | `Jelena-<AG>`, avvio manuale, account configurato (verificato dopo la creazione) |
| 10 | Task CSU | importato da XML, ogni minuto, secondi sfalsati per AG, istanze multiple ignorate |
| 11 | `disksign.ini` | sui mount point presenti; **rifiuta di reclamare un disco dichiarato da un altro AG** |
| 12 | Database | crea `Jelena` (SIMPLE), schema, registra AG e nodi (`is_local` corretto), `ConnectVCenter`, **scopre i dischi in vCenter** (UUID, chiave SCSI, capacità), sul nodo che li possiede crea `Dummy01` e registra i database |

## 4. Cluster
Dopo aver installato **tutti** i nodi:
```
VhaSetup cluster --config vha-deployment.json      # sul primo nodo: crea il cluster (senza storage) + witness + i ruoli di cui è proprietario iniziale
VhaSetup cluster --config vha-deployment.json      # sugli altri nodi proprietari iniziali di un AG: creano i loro ruoli
```
Un ruolo viene creato **dal nodo che possiede i dischi**, così il primo avvio del servizio non sposta nulla. Il gruppo è rinominato col nome dell'AG (il servizio lo sposta per nome).

## 5. Verifica
```
VhaSetup check --config vha-deployment.json         # = ClusteredService.exe --check Jelena-<AG> per ogni AG
sql\05_health_check.sql                             # su ogni nodo: zero righe = configurazione coerente
```

## 6. Collaudo obbligatorio su ambiente di prova
**Operazioni Windows da verificare la prima volta** (ipotesi non provate su Windows):
`sc.exe config/create … obj=` con un gMSA senza password; `Register-ScheduledTask -Xml … -User <gMSA$>`; `secedit /configure /areas USER_RIGHTS`
(una GPO di dominio può sovrascrivere i diritti); `New-Cluster -NoStorage`/`Add-ClusterGenericServiceRole` con CNO; corrispondenza
numero di serie Windows ↔ UUID VMware senza trattini; `Set-PowerCLIConfiguration -Scope AllUsers`.

Scenari di failover (con DB di prova a più file/filegroup, due AG sulla stessa istanza):
1. Soft test: ruolo offline/online sul proprietario ⇒ nessuna interruzione. Hard test: sposta il ruolo ⇒ DB online, `JDisks/SCN/CurrentOwner` coerenti, task CSU riabilitato.
2. Proprietario **spento**; proprietario **acceso con SQL fermo** (`Force` e `Abort`); proprietario irraggiungibile in rete.
3. Servizio interrotto a metà (dopo lo stacco dei dischi): al riavvio deve ricomporre da solo.
4. VM con snapshot ⇒ failback automatico.
5. Nodo remoto spento durante il CSU; DB creato mentre l'altro nodo è giù.
6. Perdita del solo `.ldf` di un DB gestito ⇒ **nessun** `DROP DATABASE`.
7. Patch rotante SQL (passivo → failover → attivo) con vSphere HA/DRS attivi e regola di anti-affinità.
8. Ripristino dei backup (la sola protezione dalla corruzione dei dati) e backup su share condivisa, non su `D:\` locale.

## 7. Migrazione a SQL Server 2025 / vSphere 8
* Il meccanismo (detach/attach) non dipende dalla versione, ma la **build deve essere identica** su tutti i nodi: un database agganciato a una build più recente non torna su una più vecchia. Aggiornare prima il passivo, poi failover, poi l'attivo.
* TLS: l'app usa i default del sistema (TLS 1.2/1.3). Con certificati autofirmati serve `Encrypt=True;TrustServerCertificate=True` in `sqlOptions` (valore predefinito, segnalato come avviso); con un certificato attendibile rimuovere `TrustServerCertificate`.
* PowerCLI 13 + vSphere 8: solo https e modulo (non snap-in); solo controller SCSI (NVMe/SATA non supportati, errore esplicito).
* Licenze: vedi `ANALISI.md` §C (diritti di failover passivo richiedono Software Assurance).

## 8. Installazione manuale (senza setup)
Restano valide le parti 2.0.0 per cui non c'è automazione (AD, pre-staging del CNO, layout dei dischi). `jelena.ini` e script SQL: vedi `sql\` e `docs\ANALISI.md`.
Chiavi `jelena.ini`: `[AG]` `Instance` (anche stringa di connessione completa se contiene `=`), `SqlOptions`; `[Failover]` `OwnerUnreachablePolicy`, `CsuWaitSeconds`, `FailoverTimeoutSeconds`, `ReleaseTimeoutSeconds`; `[General]` `LogRetentionDays`.
