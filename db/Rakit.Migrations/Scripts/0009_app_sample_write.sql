-- Sample write procedures for app.Customers (F3b). Convention: RETURN 0 = success, any other value = business error code.
CREATE OR ALTER PROCEDURE app.SpCustomers_Insert
    @Name    NVARCHAR(100),
    @Email   NVARCHAR(200) = NULL,
    @NewId   INT           = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @Email IS NOT NULL AND EXISTS (SELECT 1 FROM app.Customers WHERE Email = @Email)
        THROW 50001, N'Email sudah dipakai pelanggan lain.', 1;

    INSERT INTO app.Customers (Name, Email) VALUES (@Name, @Email);
    SET @NewId = CONVERT(INT, SCOPE_IDENTITY());

    RETURN 0;
END
GO

CREATE OR ALTER PROCEDURE app.SpCustomers_Update
    @Id      INT,
    @Name    NVARCHAR(100),
    @Email   NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM app.Customers WHERE Id = @Id)
        RETURN 1;

    IF @Email IS NOT NULL AND EXISTS (SELECT 1 FROM app.Customers WHERE Email = @Email AND Id <> @Id)
        THROW 50001, N'Email sudah dipakai pelanggan lain.', 1;

    UPDATE app.Customers SET Name = @Name, Email = @Email WHERE Id = @Id;

    RETURN 0;
END
GO

CREATE OR ALTER PROCEDURE app.SpCustomers_Delete
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM app.Customers WHERE Id = @Id)
        RETURN 1;

    DELETE FROM app.Customers WHERE Id = @Id;

    RETURN 0;
END
GO

-- Stored procedures are default-deny (see 0006): list the new sample procedures so the dev database works out of the box.
INSERT INTO rakit.SpWhitelist (Id, SchemaName, ProcName, IsActived, CreatedBy, Created)
SELECT NEWID(), v.SchemaName, v.ProcName, 1, N'migration', SYSUTCDATETIME()
FROM (VALUES (N'app', N'SpCustomers_Insert'), (N'app', N'SpCustomers_Update'), (N'app', N'SpCustomers_Delete')) AS v (SchemaName, ProcName)
WHERE NOT EXISTS (
    SELECT 1 FROM rakit.SpWhitelist w WHERE w.SchemaName = v.SchemaName AND w.ProcName = v.ProcName);
