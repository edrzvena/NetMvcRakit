-- The state of a workflow run that is waiting for the visitor (an Action step shows a form and the run continues when a button is
-- pressed). State is the run's JSON ({"form", "key", "claims", "now", "outputs", "runBy"}); a row is valid until Expires and is used once.
IF OBJECT_ID(N'rakit.WorkflowRuns', N'U') IS NULL
BEGIN
    CREATE TABLE rakit.WorkflowRuns (
        RunId       uniqueidentifier NOT NULL CONSTRAINT PK_WorkflowRuns PRIMARY KEY,
        WorkflowId  uniqueidentifier NOT NULL CONSTRAINT FK_WorkflowRuns_Workflows REFERENCES rakit.Workflows (Id),
        ModuleId    uniqueidentifier NULL,
        ActionCode  nvarchar(50)     NULL,
        StepCode    nvarchar(50)     NOT NULL,
        State       nvarchar(max)    NOT NULL,
        Status      nvarchar(20)     NOT NULL CONSTRAINT DF_WorkflowRuns_Status DEFAULT N'pending',
        Created     datetime2        NOT NULL,
        Expires     datetime2        NOT NULL,
        CONSTRAINT CK_WorkflowRuns_State_Json CHECK (ISJSON(State) = 1),
        CONSTRAINT CK_WorkflowRuns_Status CHECK (Status IN (N'pending', N'done'))
    );

    CREATE INDEX IX_WorkflowRuns_Expires ON rakit.WorkflowRuns (Expires);
END
GO
