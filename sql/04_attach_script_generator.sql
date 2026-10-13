/*
  Generates CREATE DATABASE ... FOR ATTACH from dbo.JDBFiles (replacement of Attach_Database.sql, which read a non-existing
  table "jdbfiles" lower case, only file_id 1 and 2, returned NULL (= no script) when file_id 2 was missing and did not escape names).
  All files (secondary data files, additional logs) are listed, ordered by file_id. For inspection and manual recovery:
  the clustered service builds the same statement itself.
*/
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;   -- required by the XML .value() method; sqlcmd starts with it OFF
USE [Jelena];
SELECT f.JAGroupsID, f.DBName,
       'CREATE DATABASE ' + QUOTENAME(f.DBName) + ' ON ' + CHAR(13) + CHAR(10)
       + STUFF((SELECT ',' + CHAR(13) + CHAR(10) + '(FILENAME = N''' + REPLACE(x.PhysicalName, '''', '''''') + ''')'
                FROM dbo.JDBFiles x WHERE x.JAGroupsID = f.JAGroupsID AND x.DBName = f.DBName ORDER BY x.file_id
                FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 3, '')
       + CHAR(13) + CHAR(10) + 'FOR ATTACH' AS AttachScript
FROM dbo.JDBFiles f
GROUP BY f.JAGroupsID, f.DBName
HAVING MIN(f.file_id) = 1;     -- a database without its primary file description is not scripted
GO
