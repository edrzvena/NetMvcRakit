CREATE OR ALTER PROCEDURE app.SpCustomers_Detail
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, Name, Email, Created
    FROM app.Customers
    WHERE Id = @Id;
END
