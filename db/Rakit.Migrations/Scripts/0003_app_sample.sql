IF OBJECT_ID(N'app.Customers', N'U') IS NULL
BEGIN
    CREATE TABLE app.Customers (
        Id      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
        Name    nvarchar(100) NOT NULL,
        Email   nvarchar(200) NULL,
        Created datetime2     NOT NULL CONSTRAINT DF_Customers_Created DEFAULT SYSUTCDATETIME()
    );

    INSERT INTO app.Customers (Name, Email) VALUES
        (N'Budi Santoso',  N'budi@example.com'),
        (N'Siti Rahma',    N'siti@example.com'),
        (N'Andi Wijaya',   N'andi@example.com'),
        (N'Dewi Lestari',  NULL),
        (N'Eko Prasetyo',  N'eko@example.com'),
        (N'Fitri Handayani', N'fitri@example.com');
END
GO

CREATE OR ALTER PROCEDURE app.SpCustomers_List
    @IsCount  BIT           = 0,
    @Skip     INT           = 0,
    @Take     INT           = 10,
    @OrderBy  NVARCHAR(50)  = N'created_desc',
    @Search   NVARCHAR(255) = N''
AS
BEGIN
    SET NOCOUNT ON;

    IF @IsCount = 1
        SELECT COUNT(1) AS Number
        FROM app.Customers
        WHERE @Search = N'' OR Name LIKE N'%' + @Search + N'%' OR Email LIKE N'%' + @Search + N'%';
    ELSE
        SELECT Id, Name, Email, Created
        FROM app.Customers
        WHERE @Search = N'' OR Name LIKE N'%' + @Search + N'%' OR Email LIKE N'%' + @Search + N'%'
        ORDER BY
            CASE WHEN @OrderBy = N'name_asc'     THEN Name    END ASC,
            CASE WHEN @OrderBy = N'name_desc'    THEN Name    END DESC,
            CASE WHEN @OrderBy = N'created_asc'  THEN Created END ASC,
            CASE WHEN @OrderBy NOT IN (N'name_asc', N'name_desc', N'created_asc') THEN Created END DESC,
            Id
        OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END
