IF OBJECT_ID(N'rakit.ModuleGroups', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.ModuleGroups (
        Id         uniqueidentifier NOT NULL CONSTRAINT PK_ModuleGroups PRIMARY KEY,
        Name       nvarchar(100)    NOT NULL,
        IsActived  bit              NOT NULL CONSTRAINT DF_ModuleGroups_IsActived DEFAULT 1,
        CreatedBy  nvarchar(max)    NULL,
        Created    datetime2        NOT NULL,
        UpdatedBy  nvarchar(max)    NULL,
        Updated    datetime2        NULL,
        IsDeleted  bit              NOT NULL CONSTRAINT DF_ModuleGroups_IsDeleted DEFAULT 0,
        [Order]    int              NOT NULL CONSTRAINT DF_ModuleGroups_Order DEFAULT 0
    );

    CREATE UNIQUE INDEX UX_ModuleGroups_Name ON rakit.ModuleGroups (Name) WHERE IsDeleted = 0;
END
GO

IF OBJECT_ID(N'rakit.Modules', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.Modules (
        Id          uniqueidentifier NOT NULL CONSTRAINT PK_Modules PRIMARY KEY,
        Name        nvarchar(200)    NOT NULL,
        Type        nvarchar(20)     NOT NULL,
        Description nvarchar(500)    NULL,
        Status      nvarchar(20)     NOT NULL CONSTRAINT DF_Modules_Status DEFAULT N'draft',
        GroupId     uniqueidentifier NULL CONSTRAINT FK_Modules_ModuleGroups REFERENCES rakit.ModuleGroups (Id),
        Config      nvarchar(max)    NOT NULL,
        CreatedBy   nvarchar(max)    NULL,
        Created     datetime2        NOT NULL,
        UpdatedBy   nvarchar(max)    NULL,
        Updated     datetime2        NULL,
        IsDeleted   bit              NOT NULL CONSTRAINT DF_Modules_IsDeleted DEFAULT 0,
        [Order]     int              NOT NULL CONSTRAINT DF_Modules_Order DEFAULT 0,
        CONSTRAINT CK_Modules_Type CHECK (Type IN (N'table')),
        CONSTRAINT CK_Modules_Status CHECK (Status IN (N'draft', N'published')),
        CONSTRAINT CK_Modules_Config_Json CHECK (ISJSON(Config) = 1)
    );

    CREATE INDEX IX_Modules_GroupId ON rakit.Modules (GroupId) WHERE IsDeleted = 0;
END
GO

IF OBJECT_ID(N'rakit.Menus', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.Menus (
        Id          uniqueidentifier NOT NULL CONSTRAINT PK_Menus PRIMARY KEY,
        Label       nvarchar(100)    NOT NULL,
        Icon        nvarchar(50)     NULL,
        ModuleId    uniqueidentifier NULL CONSTRAINT FK_Menus_Modules REFERENCES rakit.Modules (Id),
        Url         nvarchar(500)    NULL,
        ParentId    uniqueidentifier NULL CONSTRAINT FK_Menus_Parent REFERENCES rakit.Menus (Id),
        IsDelimiter bit              NOT NULL CONSTRAINT DF_Menus_IsDelimiter DEFAULT 0,
        IsActived   bit              NOT NULL CONSTRAINT DF_Menus_IsActived DEFAULT 1,
        CreatedBy   nvarchar(max)    NULL,
        Created     datetime2        NOT NULL,
        UpdatedBy   nvarchar(max)    NULL,
        Updated     datetime2        NULL,
        IsDeleted   bit              NOT NULL CONSTRAINT DF_Menus_IsDeleted DEFAULT 0,
        [Order]     int              NOT NULL CONSTRAINT DF_Menus_Order DEFAULT 0,
        -- a delimiter points nowhere; every other item points at exactly one of a module or a URL
        CONSTRAINT CK_Menus_Target CHECK (
            (IsDelimiter = 1 AND ModuleId IS NULL AND Url IS NULL)
            OR (IsDelimiter = 0 AND ((ModuleId IS NOT NULL AND Url IS NULL) OR (ModuleId IS NULL AND Url IS NOT NULL)))),
        CONSTRAINT CK_Menus_NotOwnParent CHECK (ParentId IS NULL OR ParentId <> Id)
    );

    CREATE INDEX IX_Menus_ModuleId ON rakit.Menus (ModuleId) WHERE IsDeleted = 0;
    CREATE INDEX IX_Menus_ParentId ON rakit.Menus (ParentId) WHERE IsDeleted = 0;
END
GO
