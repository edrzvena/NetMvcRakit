IF OBJECT_ID(N'rakit.DataSources', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.DataSources (
        Id            uniqueidentifier NOT NULL CONSTRAINT PK_DataSources PRIMARY KEY,
        Code          nvarchar(50)     NOT NULL,
        Name          nvarchar(200)    NOT NULL,
        Mode          nvarchar(20)     NOT NULL,
        StatementType nvarchar(20)     NOT NULL,
        Version       int              NOT NULL,
        Definition    nvarchar(max)    NOT NULL,
        CreatedBy     nvarchar(max)    NULL,
        Created       datetime2        NOT NULL,
        UpdatedBy     nvarchar(max)    NULL,
        Updated       datetime2        NULL,
        IsDeleted     bit              NOT NULL CONSTRAINT DF_DataSources_IsDeleted DEFAULT 0,
        [Order]       int              NOT NULL CONSTRAINT DF_DataSources_Order DEFAULT 0,
        CONSTRAINT CK_DataSources_Definition_Json CHECK (ISJSON(Definition) = 1),
        CONSTRAINT CK_DataSources_Mode CHECK (Mode IN (N'table', N'query', N'sp')),
        CONSTRAINT CK_DataSources_StatementType CHECK (StatementType IN (N'query', N'command'))
    );

    CREATE UNIQUE INDEX UX_DataSources_Code ON rakit.DataSources (Code) WHERE IsDeleted = 0;
END

IF OBJECT_ID(N'rakit.DataSourceVersions', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.DataSourceVersions (
        Id            uniqueidentifier NOT NULL CONSTRAINT PK_DataSourceVersions PRIMARY KEY,
        DataSourceId  uniqueidentifier NOT NULL
            CONSTRAINT FK_DataSourceVersions_DataSources REFERENCES rakit.DataSources (Id),
        Version       int              NOT NULL,
        Name          nvarchar(200)    NOT NULL,
        Mode          nvarchar(20)     NOT NULL,
        StatementType nvarchar(20)     NOT NULL,
        Definition    nvarchar(max)    NOT NULL,
        ChangedBy     nvarchar(max)    NULL,
        Changed       datetime2        NOT NULL,
        CONSTRAINT UQ_DataSourceVersions UNIQUE (DataSourceId, Version)
    );
END
