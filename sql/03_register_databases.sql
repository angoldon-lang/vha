/*
  Registers the user databases of an availability group in dbo.JDatabases and bumps the SCN.
  Replaces the query of the 2.0.0 guide, which: hard coded JAGroupsID = 1 (wrong with several AGs on one instance),
  did not exclude "Dummy01" (it excluded 'dummy'), inserted one row per data FILE (secondary .ndf included) and did not escape names.

  Run it on the node that currently owns the disks. Only databases whose files ALL live under @MountRoot are registered:
  a database with a file elsewhere would not follow the disks and would fail at the first failover.
    sqlcmd -S . -d Jelena -i 03_register_databases.sql -v AgName="isql2016a-AG01" MountRoot="M:\isql2016a-AG01\"
*/
SET NOCOUNT ON; SET XACT_ABORT ON;
USE [Jelena];
DECLARE @AgName NVARCHAR(128) = N'$(AgName)', @Root NVARCHAR(400) = N'$(MountRoot)';
DECLARE @AgId INT = (SELECT ID FROM dbo.JAGroups WHERE Name = @AgName);
IF @AgId IS NULL THROW 50010, 'Availability group not registered (run 02_register_availability_group.sql first)', 1;

DECLARE @candidates TABLE (DBName SYSNAME PRIMARY KEY, PrimaryFile NVARCHAR(400), OutsideFiles INT);
INSERT @candidates
SELECT d.name,
       MAX(CASE WHEN mf.file_id = 1 THEN mf.physical_name END),
       SUM(CASE WHEN mf.physical_name LIKE @Root + N'%' THEN 0 ELSE 1 END)
FROM sys.databases d JOIN sys.master_files mf ON mf.database_id = d.database_id
WHERE d.database_id > 4 AND d.name NOT IN (N'Jelena', N'distribution', N'admin') AND d.state = 0
GROUP BY d.name;

SELECT DBName, 'NOT registered: has ' + CAST(OutsideFiles AS VARCHAR(10)) + ' file(s) outside ' + @Root AS Problem FROM @candidates WHERE OutsideFiles > 0;

BEGIN TRAN;
INSERT dbo.JDatabases (JAGroupsID, DBName, PrimaryFile)
SELECT @AgId, c.DBName, c.PrimaryFile FROM @candidates c
WHERE c.OutsideFiles = 0 AND NOT EXISTS (SELECT 1 FROM dbo.JDatabases j WHERE j.JAGroupsID = @AgId AND j.DBName = c.DBName);
IF @@ROWCOUNT > 0
    INSERT dbo.SCN (JAGroupsID, LastSCN) SELECT @AgId, ISNULL(MAX(LastSCN), 0) + 1 FROM dbo.SCN WHERE JAGroupsID = @AgId;   -- tells the other nodes to replicate
COMMIT;

SELECT j.DBName, j.PrimaryFile FROM dbo.JDatabases j WHERE j.JAGroupsID = @AgId ORDER BY j.DBName;
GO
