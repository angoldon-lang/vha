# Requisiti di licenza e matrice di compatibilità

> **Avvertenza.** La parte sulle licenze è una guida tecnica per preparare il confronto con il rivenditore o con Microsoft/Broadcom: **non è una consulenza legale** e le condizioni cambiano con i contratti (Enterprise Agreement, CSP, SPLA) e con le versioni dei Product Terms. Le affermazioni sono da far confermare per iscritto prima dell'acquisto. La matrice di compatibilità distingue ciò che è stato **provato** da ciò che è solo **atteso**.

## 1. Come vHA usa le risorse (rilevante per la licenza)
- Ogni nodo è una VM Windows con una **propria istanza SQL Server in esecuzione** (almeno per il database di configurazione `Jelena`).
- Per ogni availability group i dischi (VMDK) con i database sono collegati **a una sola VM per volta**; sull'altra VM i database sono staccati.
- Non si usano Availability Group di SQL Server né Failover Cluster Instance: la funzione di alta affidabilità è fornita da vHA, Windows Server Failover Clustering (solo come gestore del ruolo) e vCenter.
- I database non sono mai aperti contemporaneamente su due nodi per lo stesso gruppo; in uno scenario attivo/attivo si può avere un gruppo attivo sul nodo A e un altro sul nodo B.

## 2. SQL Server
| Tema | Requisito |
|---|---|
| Edizione | **Standard** è sufficiente: il motore usa solo funzioni presenti in Standard (attach/detach, offline/online). Enterprise non è necessaria. Express/Web non sono indicate (limiti, nessuna garanzia di diritti di failover). Developer solo per prove. |
| Modalità | **Per core** (consigliata): minimo **4 core per VM** (per istanza). In alternativa Server + CAL solo se la VM ha pochi utenti/dispositivi e non supera i limiti dell'edizione. |
| VM | Si licenziano i **core virtuali (vCPU) di ogni VM** che esegue SQL Server, con minimo 4 per VM. Con Standard non esiste l'opzione "licenza tutti i core dell'host per VM illimitate": quella è un diritto di Enterprise con Software Assurance. |
| Nodo passivo | Il diritto di eseguire un'istanza passiva senza licenza dedicata (**failover rights**) richiede **Software Assurance**, vale per una sola istanza passiva per l'alta affidabilità, e **solo se** quell'istanza non serve carichi (query, report, backup, manutenzione oltre quanto consentito dai Product Terms). **Da confermare con Microsoft/rivenditore** che il modello vHA (istanza accesa con il solo database `Jelena`, database staccati) rientri nella definizione di istanza passiva. Se il nodo secondario ospita anche un gruppo attivo (scenario attivo/attivo) **entrambe le VM vanno licenziate per intero**. |
| Mobilità | Spostare i dischi tra VM non sposta licenze; lo spostamento della VM tra host (vMotion) richiede **License Mobility via Software Assurance** se i server host sono diversi in un contratto che non lo prevede già. |
| Limiti Standard 2025 | Fino a 4 socket o 32 core per istanza, buffer pool massimo 256 GB. Nessun limite di database da vHA stesso. Verificare i limiti dell'edizione sul sito Microsoft per la versione scelta. |
| Esempio | 2 VM da 8 vCPU: **16 core** SQL Server Standard (con SA per beneficiare del passivo gratuito, il costo ricade sulla sola VM attiva: 8 core). Se anche il secondo nodo ospita un gruppo attivo: 16 core. |
| Backup/monitoraggio | Qualsiasi altra istanza SQL Server usata per gestione/backup è da licenziare a parte. |

## 3. Windows Server
- **Windows Server Standard o Datacenter** per ogni VM (il clustering WSFC è incluso in entrambe). Licenza per **core dell'host fisico** (minimo 16 core per host, 8 per CPU); Standard copre 2 VM per host licenziato, Datacenter illimitate.
- **CAL Windows Server** per gli utenti/dispositivi che accedono al server, se non già coperte.
- Active Directory: i nodi devono appartenere allo stesso dominio (la creazione del cluster usa il dominio). Un gMSA è opzionale ma consigliato.

## 4. VMware vSphere
- vHA usa le API di vCenter in **scrittura** (ReconfigVM). Servono quindi un **vCenter Server** licenziato e host ESXi con una licenza che consenta l'accesso API completo: l'ESXi gratuito (solo lettura via API) **non è utilizzabile**.
- L'edizione deve includere: hot-add/hot-remove di dischi, vMotion non necessario ma consigliato, **vSphere HA** consigliato (vedi `docs/VCENTER.md`). Le edizioni e i bundle (Standard/Enterprise Plus, vSphere Foundation, Cloud Foundation) sono in **abbonamento per core** secondo i listini Broadcom correnti: verificare quale bundle copre i servizi necessari.
- **PowerCLI** è gratuito (VMware.PowerCLI da PowerShell Gallery) e viene installato dal setup.
- Storage: datastore **condiviso e visibile a tutti gli host** su cui possono girare le VM del gruppo.

## 5. Software di terze parti incluso
| Componente | Licenza | Nota |
|---|---|---|
| .NET Framework 4.8 | Gratuito (componente Windows) | Installato dal setup se manca |
| VMware PowerCLI | Gratuito, licenza VMware | Termini del modulo PowerShell Gallery |
| TaskScheduler (Microsoft.Win32.TaskScheduler 2.10.1) | MIT | Ridistribuito nel pacchetto |
| System.Text.Json 8.0.5 | MIT | Ridistribuito nel pacchetto |
| Codice vHA | Da definire | Questa versione è una **riscrittura** realizzata a partire dall'analisi del comportamento di vHA 2.0.0 (di cui non sono stati verificati i termini di licenza). Chiarire con il titolare del prodotto originale diritti, titolarità e condizioni d'uso prima della distribuzione a terzi. |

## 6. Matrice di compatibilità
Legenda: **Provato** = eseguito nei test automatici di questo progetto · **Atteso** = compatibile per progettazione, non eseguito · **No** = non supportato · **?** = non valutato.

### 6.1 SQL Server
| Versione | Stato | Note |
|---|---|---|
| SQL Server **2025** (17.x) | **Provato** su SQL Server 2025 per Linux (container, due istanze): attach/detach, failover con dati reali, crash, istanza congelata, prova di durata. **Non provato su Windows.** | Il codice SQL è lo stesso. |
| SQL Server **2022** (16.x) | **Provato** su SQL Server 2022 per Linux (container, due istanze): stessa suite d'integrazione del 2025, tutti i test verdi, nessuna riga persa nella prova di durata (20 failover con scritture continue). Non provato su Windows. | Immagine `mssql/server:2022-latest`. |
| SQL Server **2019** (15.x) | **Provato** come sopra (Linux). Non provato su Windows. | Immagine `mssql/server:2019-latest`. |
| SQL Server **2017** (14.x) | **Provato** come sopra (Linux): 204 test su 204. Non provato su Windows. Fuori dal supporto mainstream Microsoft: non consigliato per nuove installazioni. | Immagine `mssql/server:2017-latest`. |
| SQL Server 2016 | **Atteso**, non provato | Stesse istruzioni T-SQL. |
| 2014 e precedenti | **No / ?** | Non valutati. |
| Edizioni | Standard, Enterprise: atteso · Express/Web: non indicate | |
| Compatibilità database | Qualsiasi livello supportato dalla versione | I database non cambiano livello con lo spostamento. |

### 6.2 Windows Server e .NET
| Componente | Stato |
|---|---|
| Windows Server **2022 / 2025** | **Atteso** (.NET Framework 4.8/4.8.1 incluso). Non provato su Windows. |
| Windows Server **2019** | **Atteso** (richiede .NET Framework 4.8, installato dal setup). |
| Windows Server 2016 | **Atteso, non consigliato** (fuori supporto mainstream; PowerShell 5.1 e .NET 4.8). |
| Windows Server 2012 R2 e precedenti | **No** |
| PowerShell | **5.1** (Windows PowerShell) obbligatorio sui nodi; i programmi sono .NET Framework 4.8 x64. |
| Cluster | WSFC con ruolo generico per servizio `Jelena-<AG>`: **atteso**, creazione automatica tramite `VhaSetup cluster` non provata su Windows. |

### 6.3 VMware
| Componente | Stato | Note |
|---|---|---|
| vSphere / vCenter **8.0** | **Atteso** | Le chiamate PowerCLI sono simulate nei test; da collaudare su un ambiente reale. |
| vSphere / vCenter **7.0** | **Atteso** | Come sopra. |
| vSphere 6.7 e precedenti | **?** | Non valutato. |
| vSphere **9.x** | **?** | Verificare la matrice di interoperabilità PowerCLI/vSphere. |
| PowerCLI | **13.x** (richiede PowerShell 5.1) | Verificare nel documento di compatibilità PowerCLI la versione adatta al proprio vCenter. |
| Controller SCSI | **Supportati**: LSI Logic SAS, PVSCSI (disk con chiave 2000+16·controller+unità, unità 7 riservata) | |
| Controller **NVMe / SATA** | **No** | Chiavi dispositivo non gestite. |
| Dischi **RDM** / multi-writer | **No** | I dischi sono VMDK collegati a una VM per volta. |
| Snapshot sulla VM che detiene i dischi | **Non ammessi**: il failover rimanda il gruppo al nodo originario | |
| Datastore | VMFS: **atteso**. NFS, vSAN, vVols: **?** | Il disco viene identificato per UUID, non per percorso. |
| Parametro VM | `disk.EnableUUID = TRUE` obbligatorio (controllato da `--check`) | |

### 6.4 Ambiente di prova realmente usato
- SQL Server **2025, 2022, 2019, 2017 per Linux** (Docker), due istanze per volta, database attach/detach reali. La prova di durata verifica l'assenza di righe perse; la velocità di scrittura varia con la versione e non è un criterio di compatibilità.
- PowerShell 7 reale con funzioni sostitutive per `Get-Disk`, `Set-Disk`, PowerCLI.
- Mono per l'esecuzione degli eseguibili impacchettati (non è .NET Framework).
- **Mai eseguito** su Windows, vCenter, ESXi o cluster reali. Per certificare questi ambienti usare `scripts/Test-VhaEnvironment.ps1` (sola lettura, produce un report da allegare alla matrice) e il collaudo di `docs/INSTALLAZIONE.md` §6. Una certificazione di vSphere 6.7/7/8 e di SQL Server su Windows richiede l'esecuzione su quegli ambienti: non si può ottenere da un sandbox Linux.

## 7. Elenco di controllo per l'acquisto
1. Scelta tra Standard per core (consigliata) e altre modalità; numero di core virtuali per VM (minimo 4).
2. **Software Assurance** sulle licenze SQL se si conta sul nodo passivo gratuito; conferma scritta che il modello vHA rientra nei failover rights.
3. Conferma dello scenario: solo attivo/passivo oppure anche attivo/attivo (in questo caso entrambe le VM sono licenziate per intero).
4. Windows Server (Standard/Datacenter) per gli host e CAL.
5. vSphere con accesso API in scrittura, vCenter, e valutazione di vCenter HA (`docs/VCENTER.md`).
6. Chiarimento sulla titolarità e licenza del codice vHA (sezione 5).
7. Collaudo su un ambiente di prova prima della produzione (`docs/INSTALLAZIONE.md` §6).
