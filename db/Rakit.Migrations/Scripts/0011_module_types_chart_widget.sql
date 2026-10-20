-- Modules can now also be Chart and Widget (F3d).
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Modules_Type' AND parent_object_id = OBJECT_ID(N'rakit.Modules'))
    ALTER TABLE rakit.Modules DROP CONSTRAINT CK_Modules_Type;
GO

ALTER TABLE rakit.Modules ADD CONSTRAINT CK_Modules_Type CHECK (Type IN (N'table', N'form', N'page', N'chart', N'widget'));
GO
