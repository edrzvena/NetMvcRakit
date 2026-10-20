-- Sample sales data for the demo dashboard (F3d charts and widgets need numbers worth drawing): 12 months x 3 channels.
-- Figures are generated deterministically, so every database gets the same data.
IF OBJECT_ID(N'app.Sales', N'U') IS NULL
BEGIN
    CREATE TABLE app.Sales (
        Id      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sales PRIMARY KEY,
        Period  date          NOT NULL,
        Channel nvarchar(30)  NOT NULL,
        Revenue decimal(18,2) NOT NULL,
        Orders  int           NOT NULL
    );

    ;WITH n AS (
        SELECT TOP (12) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS int) AS i FROM sys.all_objects
    ),
    c AS (
        SELECT * FROM (VALUES
            (N'Online',   120, 14, 9, 3, 185000),
            (N'Toko',      90,  6, 11, 4, 210000),
            (N'Reseller',  45,  5, 7, 2, 160000)
        ) v (Channel, Base, Slope, Wiggle, Cycle, Price)
    ),
    f AS (
        SELECT n.i, c.Channel, c.Price,
               c.Base + n.i * c.Slope + (n.i % c.Cycle) * c.Wiggle AS Orders
        FROM n CROSS JOIN c
    )
    INSERT INTO app.Sales (Period, Channel, Revenue, Orders)
    SELECT DATEADD(MONTH, i, CAST('2025-11-01' AS date)), Channel,
           CAST(Orders AS decimal(18,2)) * (Price + i * 1200), Orders
    FROM f;
END
GO

CREATE OR ALTER PROCEDURE app.SpSales_List
    @IsCount  BIT           = 0,
    @Skip     INT           = 0,
    @Take     INT           = 10,
    @OrderBy  NVARCHAR(50)  = N'period_desc',
    @Search   NVARCHAR(255) = N''
AS
BEGIN
    SET NOCOUNT ON;

    IF @IsCount = 1
        SELECT COUNT(1) AS Number
        FROM app.Sales
        WHERE @Search = N'' OR Channel LIKE N'%' + @Search + N'%' OR CONVERT(char(7), Period, 126) LIKE N'%' + @Search + N'%';
    ELSE
        SELECT Id, CONVERT(char(7), Period, 126) AS Bulan, Channel AS Kanal, Revenue AS Pendapatan, Orders AS Pesanan
        FROM app.Sales
        WHERE @Search = N'' OR Channel LIKE N'%' + @Search + N'%' OR CONVERT(char(7), Period, 126) LIKE N'%' + @Search + N'%'
        ORDER BY
            CASE WHEN @OrderBy = N'bulan_asc'       THEN Period   END ASC,
            CASE WHEN @OrderBy = N'bulan_desc'      THEN Period   END DESC,
            CASE WHEN @OrderBy = N'kanal_asc'       THEN Channel  END ASC,
            CASE WHEN @OrderBy = N'kanal_desc'      THEN Channel  END DESC,
            CASE WHEN @OrderBy = N'pendapatan_asc'  THEN Revenue  END ASC,
            CASE WHEN @OrderBy = N'pendapatan_desc' THEN Revenue  END DESC,
            CASE WHEN @OrderBy = N'pesanan_asc'     THEN Orders   END ASC,
            CASE WHEN @OrderBy = N'pesanan_desc'    THEN Orders   END DESC,
            Period DESC, Id
        OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END
GO

-- One row: the headline numbers (money in millions of rupiah).
CREATE OR ALTER PROCEDURE app.SpSales_Summary
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @last date = (SELECT MAX(Period) FROM app.Sales);
    DECLARE @prev date = DATEADD(MONTH, -1, @last);

    SELECT
        CAST(SUM(Revenue) / 1000000.0 AS decimal(18,1))                                         AS TotalPendapatan,
        SUM(Orders)                                                                             AS TotalPesanan,
        CAST(SUM(Revenue) / NULLIF(SUM(Orders), 0) AS decimal(18,0))                            AS RataRataPesanan,
        CAST(SUM(CASE WHEN Period = @last THEN Revenue END) / 1000000.0 AS decimal(18,1))       AS PendapatanBulanIni,
        CAST(100.0 * (SUM(CASE WHEN Period = @last THEN Revenue END) - SUM(CASE WHEN Period = @prev THEN Revenue END))
             / NULLIF(SUM(CASE WHEN Period = @prev THEN Revenue END), 0) AS decimal(9,1))       AS Pertumbuhan
    FROM app.Sales;
END
GO

CREATE OR ALTER PROCEDURE app.SpSales_Monthly
AS
BEGIN
    SET NOCOUNT ON;

    SELECT CONVERT(char(7), Period, 126) AS Bulan,
           CAST(SUM(Revenue) / 1000000.0 AS decimal(18,1)) AS Pendapatan,
           SUM(Orders) AS Pesanan
    FROM app.Sales
    GROUP BY Period
    ORDER BY Period;
END
GO

-- Revenue per month, one column per channel (millions of rupiah), for a stacked chart.
CREATE OR ALTER PROCEDURE app.SpSales_MonthlyByChannel
AS
BEGIN
    SET NOCOUNT ON;

    SELECT CONVERT(char(7), Period, 126) AS Bulan,
           CAST(SUM(CASE WHEN Channel = N'Online'   THEN Revenue END) / 1000000.0 AS decimal(18,1)) AS Online,
           CAST(SUM(CASE WHEN Channel = N'Toko'     THEN Revenue END) / 1000000.0 AS decimal(18,1)) AS Toko,
           CAST(SUM(CASE WHEN Channel = N'Reseller' THEN Revenue END) / 1000000.0 AS decimal(18,1)) AS Reseller
    FROM app.Sales
    GROUP BY Period
    ORDER BY Period;
END
GO

CREATE OR ALTER PROCEDURE app.SpSales_ByChannel
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Channel AS Kanal, CAST(SUM(Revenue) / 1000000.0 AS decimal(18,1)) AS Pendapatan
    FROM app.Sales
    GROUP BY Channel
    ORDER BY SUM(Revenue) DESC;
END
GO

-- Stored procedures are default-deny (see 0006): list the sample ones so the dev database works out of the box.
INSERT INTO rakit.SpWhitelist (Id, SchemaName, ProcName, IsActived, CreatedBy, Created)
SELECT NEWID(), v.SchemaName, v.ProcName, 1, N'migration', SYSUTCDATETIME()
FROM (VALUES
    (N'app', N'SpSales_List'), (N'app', N'SpSales_Summary'), (N'app', N'SpSales_Monthly'),
    (N'app', N'SpSales_MonthlyByChannel'), (N'app', N'SpSales_ByChannel')
) AS v (SchemaName, ProcName)
WHERE NOT EXISTS (
    SELECT 1 FROM rakit.SpWhitelist w WHERE w.SchemaName = v.SchemaName AND w.ProcName = v.ProcName);
GO
