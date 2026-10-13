/*
  Jelena configuration database - schema (RECONSTRUCTED from the 2.0.0 binaries, not from an original DDL).
  Verify it against a working Jelena database before using it on an existing installation:
  every statement is guarded by IF OBJECT_ID(...) IS NULL, so running it on an existing database changes nothing.
  JDBFiles, JDBFileGroups and JXLog are normally created by CsuCmd.exe (the same definitions are used here).
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.JSettings', N'U') IS NULL
    CREATE TABLE dbo.JSettings (
        SettingName  VARCHAR(100)  NOT NULL CONSTRAINT PK_JSettings PRIMARY KEY,
        SettingValue NVARCHAR(MAX) NULL);

IF OBJECT_ID(N'dbo.JAGroups', N'U') IS NULL
    CREATE TABLE dbo.JAGroups (
        ID             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_JAGroups PRIMARY KEY,
        Name           VARCHAR(128)  NOT NULL CONSTRAINT UQ_JAGroups_Name UNIQUE,
        CurrentOwner   VARCHAR(128)  NULL,          -- VM name, or ##transitioning##
        Is_CSU_Working CHAR(1)       NOT NULL CONSTRAINT DF_JAGroups_Csu DEFAULT ('N'),
        ResCpuMHz      INT           NOT NULL CONSTRAINT DF_JAGroups_Cpu DEFAULT (0),
        ResMemMB       INT           NOT NULL CONSTRAINT DF_JAGroups_Mem DEFAULT (0));

IF OBJECT_ID(N'dbo.JNodes', N'U') IS NULL
    CREATE TABLE dbo.JNodes (
        JAGroupsID       INT           NOT NULL CONSTRAINT FK_JNodes_AG REFERENCES dbo.JAGroups(ID),
        VMName           VARCHAR(128)  NOT NULL,    -- virtual machine name in vCenter
        DNSName          VARCHAR(255)  NOT NULL,
        ConnectionString NVARCHAR(500) NOT NULL,
        is_local         BIT           NOT NULL CONSTRAINT DF_JNodes_Local DEFAULT (0),
        CONSTRAINT PK_JNodes PRIMARY KEY (JAGroupsID, VMName));

IF OBJECT_ID(N'dbo.JDisks', N'U') IS NULL
    CREATE TABLE dbo.JDisks (
        JAGroupsID INT           NOT NULL CONSTRAINT FK_JDisks_AG REFERENCES dbo.JAGroups(ID),
        DeviceKey  VARCHAR(10)   NOT NULL,          -- 2000 + 16 * SCSI controller + unit
        FileName   NVARCHAR(400) NOT NULL,          -- [datastore] folder/file.vmdk
        CapacityGB DECIMAL(18,2) NOT NULL CONSTRAINT DF_JDisks_Cap DEFAULT (0),
        UniqueId   VARCHAR(100)  NULL,              -- disk serial number / UUID (disk.EnableUUID=TRUE)
        Attached   BIT           NOT NULL CONSTRAINT DF_JDisks_Att DEFAULT (0),
        CONSTRAINT PK_JDisks PRIMARY KEY (JAGroupsID, DeviceKey));

IF OBJECT_ID(N'dbo.JDatabases', N'U') IS NULL
    CREATE TABLE dbo.JDatabases (
        JAGroupsID  INT           NOT NULL CONSTRAINT FK_JDatabases_AG REFERENCES dbo.JAGroups(ID),
        DBName      NVARCHAR(128) NOT NULL,
        PrimaryFile NVARCHAR(400) NULL,
        CONSTRAINT PK_JDatabases PRIMARY KEY (JAGroupsID, DBName));

IF OBJECT_ID(N'dbo.SCN', N'U') IS NULL
    CREATE TABLE dbo.SCN (
        JAGroupsID INT NOT NULL CONSTRAINT FK_SCN_AG REFERENCES dbo.JAGroups(ID),
        LastSCN    INT NOT NULL,
        CONSTRAINT PK_SCN PRIMARY KEY (JAGroupsID, LastSCN));

IF OBJECT_ID(N'dbo.JDBFileGroups', N'U') IS NULL
    CREATE TABLE dbo.JDBFileGroups (
        JAGroupsID INT NOT NULL, DBName VARCHAR(100) NOT NULL, ID INT NOT NULL,
        FGName NVARCHAR(100) NOT NULL, FGTypeDesc NVARCHAR(200) NOT NULL,
        CONSTRAINT PK_JDBFileGroups PRIMARY KEY CLUSTERED (JAGroupsID, DBName, ID));

IF OBJECT_ID(N'dbo.JDBFiles', N'U') IS NULL
    CREATE TABLE dbo.JDBFiles (
        JAGroupsID INT NOT NULL, file_id INT NOT NULL, DBName VARCHAR(100) NOT NULL, JDBFileGroupsID INT NOT NULL,
        LogicalName NVARCHAR(100) NOT NULL, PhysicalName NVARCHAR(255) NOT NULL,
        CONSTRAINT PK_JDBFiles PRIMARY KEY CLUSTERED (JAGroupsID, DBName, file_id));

IF OBJECT_ID(N'dbo.JXLog', N'U') IS NULL
    CREATE TABLE dbo.JXLog (
        ID INT IDENTITY(1,1), JAGroupsID INT, MsgType VARCHAR(10), Message VARCHAR(1000),
        [date] DATETIME NOT NULL CONSTRAINT DF_JXLog_Date DEFAULT (GETDATE()));
GO
