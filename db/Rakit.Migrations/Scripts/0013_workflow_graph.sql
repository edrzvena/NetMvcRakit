-- Workflow-wide options (JSON, e.g. {"transaction":true}).
IF COL_LENGTH(N'rakit.Workflows', N'Property') IS NULL
BEGIN
    ALTER TABLE rakit.Workflows ADD Property nvarchar(max) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Workflows_Property_Json')
BEGIN
    ALTER TABLE rakit.Workflows ADD CONSTRAINT CK_Workflows_Property_Json CHECK (Property IS NULL OR ISJSON(Property) = 1);
END
GO

-- F4a steps formed a chain through ParentId. A step now names the step after it ("next" in its JSON; a Condition names
-- "onTrue" / "onFalse"), so a chain from F4a is rewritten into explicit links. ParentId stays as the first step that links to it.
UPDATE s
SET Data = JSON_MODIFY(s.Data, '$.next', c.vCode)
FROM rakit.Steps s
JOIN rakit.Steps c ON c.ParentId = s.Id AND c.IsDeleted = 0
WHERE s.IsDeleted = 0 AND JSON_VALUE(s.Data, '$.next') IS NULL;
GO
