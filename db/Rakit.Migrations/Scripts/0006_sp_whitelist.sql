IF OBJECT_ID(N'rakit.SpWhitelist', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.SpWhitelist (
        Id         uniqueidentifier NOT NULL CONSTRAINT PK_SpWhitelist PRIMARY KEY,
        SchemaName nvarchar(128)    NOT NULL,
        ProcName   nvarchar(128)    NOT NULL,
        IsActived  bit              NOT NULL CONSTRAINT DF_SpWhitelist_IsActived DEFAULT 1,
        CreatedBy  nvarchar(max)    NULL,
        Created    datetime2        NOT NULL,
        UpdatedBy  nvarchar(max)    NULL,
        Updated    datetime2        NULL,
        CONSTRAINT UQ_SpWhitelist UNIQUE (SchemaName, ProcName)
    );
END
GO

-- Stored procedures are default-deny: only active rows here can be used by a data source.
-- The sample procedures from 0003/0004 are listed so the dev database works out of the box.
INSERT INTO rakit.SpWhitelist (Id, SchemaName, ProcName, IsActived, CreatedBy, Created)
SELECT NEWID(), v.SchemaName, v.ProcName, 1, N'migration', SYSUTCDATETIME()
FROM (VALUES (N'app', N'SpCustomers_List'), (N'app', N'SpCustomers_Detail')) AS v (SchemaName, ProcName)
WHERE NOT EXISTS (
    SELECT 1 FROM rakit.SpWhitelist w WHERE w.SchemaName = v.SchemaName AND w.ProcName = v.ProcName);
