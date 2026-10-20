-- A menu item may now be a folder: no module and no URL, only a title that holds child items.
-- A delimiter still points nowhere, and no item points at both a module and a URL.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Menus_Target' AND parent_object_id = OBJECT_ID(N'rakit.Menus'))
    ALTER TABLE rakit.Menus DROP CONSTRAINT CK_Menus_Target;
GO

ALTER TABLE rakit.Menus ADD CONSTRAINT CK_Menus_Target CHECK (
    (IsDelimiter = 1 AND ModuleId IS NULL AND Url IS NULL)
    OR (IsDelimiter = 0 AND NOT (ModuleId IS NOT NULL AND Url IS NOT NULL)));
GO
