/*
  Read-only health check of the Jelena configuration, to run on every node (or from monitoring).
  Every row returned is a problem; an empty result set means the configuration is consistent.
*/
SET NOCOUNT ON;
USE [Jelena];

SELECT a.Name AS AG, 'no node marked is_local' AS Problem FROM dbo.JAGroups a
WHERE NOT EXISTS (SELECT 1 FROM dbo.JNodes n WHERE n.JAGroupsID = a.ID AND n.is_local = 1)
UNION ALL SELECT a.Name, 'more than one node marked is_local' FROM dbo.JAGroups a
WHERE (SELECT COUNT(*) FROM dbo.JNodes n WHERE n.JAGroupsID = a.ID AND n.is_local = 1) > 1
UNION ALL SELECT a.Name, 'fewer than 2 nodes' FROM dbo.JAGroups a WHERE (SELECT COUNT(*) FROM dbo.JNodes n WHERE n.JAGroupsID = a.ID) < 2
UNION ALL SELECT a.Name, 'no disks' FROM dbo.JAGroups a WHERE NOT EXISTS (SELECT 1 FROM dbo.JDisks d WHERE d.JAGroupsID = a.ID)
UNION ALL SELECT a.Name, 'no databases (Dummy must always be present)' FROM dbo.JAGroups a WHERE NOT EXISTS (SELECT 1 FROM dbo.JDatabases d WHERE d.JAGroupsID = a.ID)
UNION ALL SELECT a.Name, 'disk ' + d.DeviceKey + ' has no UniqueId (set disk.EnableUUID=TRUE)' FROM dbo.JDisks d JOIN dbo.JAGroups a ON a.ID = d.JAGroupsID WHERE ISNULL(d.UniqueId, '') = ''
UNION ALL SELECT a.Name, 'disk key ' + d.DeviceKey + ' is not a SCSI key (2000-2063, unit 7 excluded)' FROM dbo.JDisks d JOIN dbo.JAGroups a ON a.ID = d.JAGroupsID
WHERE TRY_CAST(d.DeviceKey AS INT) IS NULL OR TRY_CAST(d.DeviceKey AS INT) NOT BETWEEN 2000 AND 2063 OR (TRY_CAST(d.DeviceKey AS INT) - 2000) % 16 = 7
UNION ALL SELECT a.Name, 'database ' + j.DBName + ' is registered but not described in JDBFiles (CSU has not run yet?)' FROM dbo.JDatabases j JOIN dbo.JAGroups a ON a.ID = j.JAGroupsID
WHERE NOT EXISTS (SELECT 1 FROM dbo.JDBFiles f WHERE f.JAGroupsID = j.JAGroupsID AND f.DBName = j.DBName)
UNION ALL SELECT a.Name, 'CSU flag Is_CSU_Working = Y (running, or a crashed run left it set)' FROM dbo.JAGroups a WHERE a.Is_CSU_Working = 'Y'
UNION ALL SELECT a.Name, 'owner is ' + ISNULL(a.CurrentOwner, 'NULL') + ' (transitioning or unknown)' FROM dbo.JAGroups a WHERE ISNULL(a.CurrentOwner, '##transitioning##') = '##transitioning##'
UNION ALL SELECT N'(setting)', 'JSettings.ConnectVCenter contains a clear text password: use -Password dpapi:... (CsuCmd.exe --protect)'
FROM dbo.JSettings WHERE SettingName = 'ConnectVCenter' AND SettingValue LIKE '%-Password%' AND SettingValue NOT LIKE '%-Password dpapi:%'
UNION ALL SELECT d.name, 'user database not registered in JDatabases of any availability group (it will not follow the disks)'
FROM sys.databases d WHERE d.database_id > 4 AND d.name NOT IN (N'Jelena') AND NOT EXISTS (SELECT 1 FROM dbo.JDatabases j WHERE j.DBName = d.name);
GO
