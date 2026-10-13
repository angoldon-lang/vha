# Integration lab

`tests/Vha.Infrastructure.Tests` exercises the real infrastructure code:

* **SQL**: two real SQL Server 2025 instances (docker). The shared directory `/tmp/vhashared` stands for the VMDK disks.
  The tests run the real `sql/*.sql` scripts, the config store, the node connector, the attach logic, the CSU store and a full
  failover (including a node killed with SIGKILL while a transaction is open).
* **PowerShell**: the real PowerShell engine (Microsoft.PowerShell.SDK) with simulated PowerCLI / Storage cmdlets.

```
tests/integration/start-lab.sh            # needs docker; pulls mcr.microsoft.com/mssql/server:2025-latest
dotnet test tests/Vha.Infrastructure.Tests
```
Without the containers every SQL test is **skipped** (not failed); the PowerShell tests need nothing.
What these tests do NOT cover: a real vCenter/PowerCLI, Windows (Storage cmdlets, Task Scheduler, EventLog, DPAPI, WinRM), a Windows cluster.
