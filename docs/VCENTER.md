# Se vCenter non è disponibile

vHA sposta i dischi dei database da una VM all'altra **tramite vCenter** (hot-remove / hot-add). Senza vCenter nessuno strumento può spostare un VMDK in modo sicuro tra due VM: vCenter è quindi il punto debole del failover *tra nodi*. Cosa si può fare, in ordine di efficacia:

## 1. Già in vHA: avvio locale senza vCenter (`LocalStartWithoutVCenter=Y`, predefinito)
Se all'avvio del servizio vCenter non risponde e **tutti** i dischi del gruppo sono già visibili a questa VM (confronto per seriale/UniqueId in Windows), il servizio mette online i dischi e i database **senza** vCenter e registra il proprietario. È sicuro perché un VMDK non può essere collegato a due VM insieme.
Copre: riavvio del servizio o del nodo attivo, riavvio automatico della VM da parte di vSphere HA (che funziona anche senza vCenter), reboot dopo un blackout con vCenter ancora spento.
**Non copre**: morte del nodo attivo con i dischi ancora collegati a quella VM (servirebbe vCenter per staccarli) — in quel caso il failover viene rifiutato senza modificare nulla e il log lo dice. Con vSphere HA attivo la VM morta riparte da sola su un altro host e il servizio la rimette online.
Per disattivarlo: `[Failover] LocalStartWithoutVCenter=N` in `jelena.ini`.

## 2. Rendere vCenter ridondante (infrastruttura, consigliato)
- **vCenter High Availability (VCHA)**: tre nodi (attivo/passivo/witness) con IP unico; il failover di vCenter richiede ~minuti e vHA lo riprova (3 tentativi da 10 s all'avvio). È la misura più efficace.
- **Backup di vCenter** verificato e procedura di ripristino provata; **vSphere HA** attivo sul cluster (non dipende da vCenter una volta configurato).
- vCenter su un host/datastore **non** appartenente ai dischi dei database e non dipendente dagli stessi SQL.
- Monitoraggio: eseguire `ClusteredService.exe --check Jelena-<AG>` da una pianificazione e avvisare se la connessione a vCenter fallisce.

## 3. Procedura manuale di emergenza (vCenter perso, nodo attivo morto)
1. Collega i VMDK del gruppo alla VM di destinazione direttamente dall'host ESXi (client host, "Add existing hard disk") dopo aver verificato che la VM morta sia spenta.
2. Avvia il ruolo cluster sulla VM di destinazione: l'avvio locale (punto 1) mette online i database e aggiorna il proprietario.

## 4. Non implementato di proposito
- **Collegamento diretto a ESXi come ripiego automatico**: richiede di conoscere l'host di ogni VM, credenziali per ogni host, identificativi diversi da vCenter, e non è verificabile senza ambiente reale. Il rischio di un doppio collegamento supera il beneficio. Se serve, va progettato e collaudato a parte.
- **Più indirizzi vCenter**: utile solo per copie VCHA/DR dello stesso inventario; un secondo vCenter indipendente non vede le stesse VM.

## 5. Alternative strategiche
Se il requisito è "nessuna dipendenza da vCenter durante il failover": SQL Server **Failover Cluster Instance** su WSFC con dischi condivisi VMDK clusterizzati (supportata anche da Standard a 2 nodi) oppure un **Basic Availability Group** (Standard, 1 database per gruppo, 2 repliche, dischi separati per nodo). Sono architetture diverse da vHA, con licenze e costi di storage diversi.
