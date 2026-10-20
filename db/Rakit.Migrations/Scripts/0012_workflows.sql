IF OBJECT_ID(N'rakit.Workflows', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.Workflows (
        Id          uniqueidentifier NOT NULL CONSTRAINT PK_Workflows PRIMARY KEY,
        Code        nvarchar(50)     NOT NULL,
        Name        nvarchar(200)    NOT NULL,
        Description nvarchar(500)    NULL,
        Status      nvarchar(20)     NOT NULL CONSTRAINT DF_Workflows_Status DEFAULT N'draft',
        CreatedBy   nvarchar(max)    NULL,
        Created     datetime2        NOT NULL,
        UpdatedBy   nvarchar(max)    NULL,
        Updated     datetime2        NULL,
        IsDeleted   bit              NOT NULL CONSTRAINT DF_Workflows_IsDeleted DEFAULT 0,
        [Order]     int              NOT NULL CONSTRAINT DF_Workflows_Order DEFAULT 0,
        CONSTRAINT CK_Workflows_Status CHECK (Status IN (N'draft', N'published'))
    );

    CREATE UNIQUE INDEX UX_Workflows_Code ON rakit.Workflows (Code) WHERE IsDeleted = 0;
END
GO

-- One row per step. Data is the step's own JSON ({"code","name","type","dataSourceId"}); ParentId is the step before it
-- (a chain today; a branching step can be added later without changing the table).
IF OBJECT_ID(N'rakit.Steps', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.Steps (
        Id          uniqueidentifier NOT NULL CONSTRAINT PK_Steps PRIMARY KEY,
        WorkflowId  uniqueidentifier NOT NULL CONSTRAINT FK_Steps_Workflows REFERENCES rakit.Workflows (Id),
        ParentId    uniqueidentifier NULL CONSTRAINT FK_Steps_Parent REFERENCES rakit.Steps (Id),
        Data        nvarchar(max)    NOT NULL,
        vCode       AS (CONVERT(nvarchar(50), JSON_VALUE(Data, '$.code'))),
        vType       AS (CONVERT(nvarchar(30), JSON_VALUE(Data, '$.type'))),
        CreatedBy   nvarchar(max)    NULL,
        Created     datetime2        NOT NULL,
        UpdatedBy   nvarchar(max)    NULL,
        Updated     datetime2        NULL,
        IsDeleted   bit              NOT NULL CONSTRAINT DF_Steps_IsDeleted DEFAULT 0,
        [Order]     int              NOT NULL CONSTRAINT DF_Steps_Order DEFAULT 0,
        CONSTRAINT CK_Steps_Data_Json CHECK (ISJSON(Data) = 1),
        CONSTRAINT CK_Steps_NotOwnParent CHECK (ParentId IS NULL OR ParentId <> Id)
    );

    CREATE INDEX IX_Steps_WorkflowId ON rakit.Steps (WorkflowId, [Order]) WHERE IsDeleted = 0;
END
GO

-- One row per step execution. RunId groups the steps of one run. Data = {"stepCode","stepName","status","message","durationMs",
-- "moduleId","actionCode","runBy"}; the step is identified by its code because saving a workflow replaces its Steps rows.
IF OBJECT_ID(N'rakit.TrackingSteps', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.TrackingSteps (
        Id          uniqueidentifier NOT NULL CONSTRAINT PK_TrackingSteps PRIMARY KEY,
        RunId       uniqueidentifier NOT NULL,
        WorkflowId  uniqueidentifier NOT NULL CONSTRAINT FK_TrackingSteps_Workflows REFERENCES rakit.Workflows (Id),
        Data        nvarchar(max)    NOT NULL,
        vStepCode   AS (CONVERT(nvarchar(50), JSON_VALUE(Data, '$.stepCode'))),
        vStatus     AS (CONVERT(nvarchar(20), JSON_VALUE(Data, '$.status'))),
        Created     datetime2        NOT NULL,
        CONSTRAINT CK_TrackingSteps_Data_Json CHECK (ISJSON(Data) = 1)
    );

    CREATE INDEX IX_TrackingSteps_Workflow ON rakit.TrackingSteps (WorkflowId, Created DESC);
    CREATE INDEX IX_TrackingSteps_Run ON rakit.TrackingSteps (RunId);
END
GO
