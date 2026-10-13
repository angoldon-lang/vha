/*
  Registers (idempotently) one availability group, its nodes and its Dummy database.
  Run it on EVERY node, in SQLCMD mode, after restoring/creating the Jelena database, editing the variables below.
  It replaces the "scratch" SETUP_JELENA_TABLES.sql of 2.0.0, which truncated tables, used a hard coded AG id (9),
  referenced a non-existing table (itltesql02) and compared VMName with @@SERVERNAME (wrong for named instances).

  SQLCMD usage:
    sqlcmd -S . -d Jelena -i 02_register_availability_group.sql ^
      -v AgName="isql2016a-AG01" VmName="ENTGSQL01" NodeList="ENTGSQL01|entgsql01.corp.local|entgsql01\isql2016a;ENTGSQL02|entgsql02.corp.local|entgsql02\isql2016a" ^
         Owner="ENTGSQL01" CpuMHz="0" MemMB="0"
  NodeList format:  VMName|DNSName|SqlInstance ; ...   (the connection string is generated; add options below if needed)
*/
SET NOCOUNT ON; SET XACT_ABORT ON;
USE [Jelena];

DECLARE @AgName    NVARCHAR(128) = N'$(AgName)';
DECLARE @VmName    NVARCHAR(128) = N'$(VmName)';      -- name of THIS virtual machine in vCenter
DECLARE @NodeList  NVARCHAR(MAX) = N'$(NodeList)';
DECLARE @Owner     NVARCHAR(128) = N'$(Owner)';
DECLARE @CpuMHz    INT           = $(CpuMHz);
DECLARE @MemMB     INT           = $(MemMB);
DECLARE @CsOptions NVARCHAR(200) = N'Connection Timeout=16;MultipleActiveResultSets=True;Encrypt=True;TrustServerCertificate=True';

IF @AgName = N'' OR @VmName = N'' OR @NodeList = N'' THROW 50001, 'AgName, VmName and NodeList are mandatory', 1;

BEGIN TRAN;

IF NOT EXISTS (SELECT 1 FROM dbo.JAGroups WHERE Name = @AgName)
    INSERT dbo.JAGroups (Name, CurrentOwner, Is_CSU_Working, ResCpuMHz, ResMemMB) VALUES (@AgName, @Owner, 'N', @CpuMHz, @MemMB);
ELSE
    UPDATE dbo.JAGroups SET ResCpuMHz = @CpuMHz, ResMemMB = @MemMB WHERE Name = @AgName;   -- owner is never overwritten on re-runs

DECLARE @AgId INT = (SELECT ID FROM dbo.JAGroups WHERE Name = @AgName);

DECLARE @n TABLE (VMName NVARCHAR(128), DNSName NVARCHAR(255), Instance NVARCHAR(255));

DECLARE @item NVARCHAR(1000), @p1 INT, @p2 INT;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@NodeList, ';') WHERE LTRIM(RTRIM(value)) <> N'';
OPEN c; FETCH NEXT FROM c INTO @item;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @p1 = CHARINDEX('|', @item); SET @p2 = CHARINDEX('|', @item, @p1 + 1);
    IF @p1 = 0 OR @p2 = 0 THROW 50002, 'NodeList items must be VMName|DNSName|SqlInstance', 1;
    INSERT @n VALUES (LTRIM(RTRIM(LEFT(@item, @p1 - 1))), LTRIM(RTRIM(SUBSTRING(@item, @p1 + 1, @p2 - @p1 - 1))), LTRIM(RTRIM(SUBSTRING(@item, @p2 + 1, 1000))));
    FETCH NEXT FROM c INTO @item;
END
CLOSE c; DEALLOCATE c;

IF NOT EXISTS (SELECT 1 FROM @n WHERE VMName = @VmName) THROW 50003, 'VmName is not in NodeList', 1;
IF (SELECT COUNT(*) FROM @n) < 2 THROW 50004, 'At least two nodes are required', 1;

MERGE dbo.JNodes AS t
USING (SELECT @AgId AS JAGroupsID, VMName, DNSName,
              N'Server=' + Instance + N';Database=Jelena;Trusted_Connection=True;' + @CsOptions AS ConnectionString,
              CASE WHEN VMName = @VmName THEN 1 ELSE 0 END AS is_local FROM @n) AS s
   ON t.JAGroupsID = s.JAGroupsID AND t.VMName = s.VMName
WHEN MATCHED THEN UPDATE SET DNSName = s.DNSName, ConnectionString = s.ConnectionString, is_local = s.is_local
WHEN NOT MATCHED THEN INSERT (JAGroupsID, VMName, DNSName, ConnectionString, is_local) VALUES (s.JAGroupsID, s.VMName, s.DNSName, s.ConnectionString, s.is_local);

IF NOT EXISTS (SELECT 1 FROM dbo.SCN WHERE JAGroupsID = @AgId) INSERT dbo.SCN (JAGroupsID, LastSCN) VALUES (@AgId, 1);

COMMIT;

SELECT 'AG' AS what, ID, Name, CurrentOwner FROM dbo.JAGroups WHERE ID = @AgId;
SELECT 'Nodes' AS what, VMName, DNSName, is_local FROM dbo.JNodes WHERE JAGroupsID = @AgId ORDER BY VMName;
PRINT 'Next steps: INSERT the JDisks rows (see 03_register_databases.sql / Get-VMDisk in docs/INSTALLAZIONE.md), set JSettings.ConnectVCenter, run ClusteredService.exe --check.';
GO
