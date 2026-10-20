-- Sample data for trying the Playground, data sources and modules: 21 tables and 2 views in schema [app], tens to hundreds of
-- rows each, with foreign keys, NULLs, bits, decimals, dates and a few indexes. NOT a migration: run it by hand when you want it
--   sqlcmd -S .\SQLEXPRESS -d Rakit -C -I -E -i db\Samples\sample-data.sql
-- It is repeatable: it drops and recreates only the objects below (never Customers or Sales). The data is deterministic
-- (MD5 hash based), dates are relative to today.
SET NOCOUNT ON;
GO

-- ---------- drop (children first) ----------
IF OBJECT_ID(N'app.vw_OrderSummary', N'V') IS NOT NULL DROP VIEW app.vw_OrderSummary;
IF OBJECT_ID(N'app.vw_EmployeeDirectory', N'V') IS NOT NULL DROP VIEW app.vw_EmployeeDirectory;
DECLARE @t TABLE (ord int, name sysname);
INSERT @t VALUES (1,N'Loans'),(2,N'Books'),(3,N'Enrollments'),(4,N'Students'),(5,N'Courses'),(6,N'Tickets'),(7,N'Tasks'),(8,N'Projects'),
  (9,N'Payments'),(10,N'Invoices'),(11,N'OrderItems'),(12,N'Orders'),(13,N'Buyers'),(14,N'Stores'),(15,N'Stock'),(16,N'Warehouses'),
  (17,N'Products'),(18,N'Suppliers'),(19,N'Categories'),(20,N'Employees'),(21,N'Departments');
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'IF OBJECT_ID(N''app.' + name + N''', N''U'') IS NOT NULL DROP TABLE app.' + QUOTENAME(name) + N';' FROM @t ORDER BY ord;
EXEC (@sql);
GO

-- ---------- helpers ----------
IF OBJECT_ID(N'tempdb..#n') IS NOT NULL DROP TABLE #n;
SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i INTO #n FROM sys.all_objects a CROSS JOIN sys.all_objects b;

IF OBJECT_ID(N'tempdb..#w') IS NOT NULL DROP TABLE #w;
CREATE TABLE #w (kind char(2), id int, v nvarchar(60), v2 nvarchar(60) NULL);
INSERT #w (kind, id, v) VALUES
 ('fn',1,N'Budi'),('fn',2,N'Siti'),('fn',3,N'Agus'),('fn',4,N'Dewi'),('fn',5,N'Rudi'),('fn',6,N'Ani'),('fn',7,N'Joko'),('fn',8,N'Rina'),
 ('fn',9,N'Eko'),('fn',10,N'Sari'),('fn',11,N'Dian'),('fn',12,N'Hendra'),('fn',13,N'Maya'),('fn',14,N'Fajar'),('fn',15,N'Lestari'),
 ('fn',16,N'Bambang'),('fn',17,N'Wulan'),('fn',18,N'Yusuf'),('fn',19,N'Putri'),('fn',20,N'Andi'),
 ('ln',1,N'Santoso'),('ln',2,N'Wijaya'),('ln',3,N'Pratama'),('ln',4,N'Hidayat'),('ln',5,N'Saputra'),('ln',6,N'Kusuma'),('ln',7,N'Nugroho'),
 ('ln',8,N'Lestari'),('ln',9,N'Setiawan'),('ln',10,N'Rahayu'),('ln',11,N'Gunawan'),('ln',12,N'Susanto'),('ln',13,N'Permana'),
 ('ln',14,N'Wibowo'),('ln',15,N'Hartono'),('ln',16,N'Utami'),('ln',17,N'Firmansyah'),('ln',18,N'Maulana'),('ln',19,N'Purnama'),('ln',20,N'Halim'),
 ('aj',1,N'Premium'),('aj',2,N'Compact'),('aj',3,N'Eco'),('aj',4,N'Pro'),('aj',5,N'Classic'),('aj',6,N'Smart'),('aj',7,N'Mini'),
 ('aj',8,N'Ultra'),('aj',9,N'Basic'),('aj',10,N'Deluxe'),
 ('nn',1,N'Laptop'),('nn',2,N'Mouse'),('nn',3,N'Keyboard'),('nn',4,N'Monitor'),('nn',5,N'Kursi'),('nn',6,N'Meja'),('nn',7,N'Lampu'),
 ('nn',8,N'Kipas'),('nn',9,N'Teko'),('nn',10,N'Panci'),('nn',11,N'Tas'),('nn',12,N'Sepatu'),('nn',13,N'Jaket'),('nn',14,N'Botol'),
 ('nn',15,N'Buku Tulis'),('nn',16,N'Pulpen'),('nn',17,N'Headset'),('nn',18,N'Speaker'),('nn',19,N'Charger'),('nn',20,N'Kabel');
INSERT #w (kind, id, v, v2) VALUES
 ('ct',1,N'Jakarta',N'DKI Jakarta'),('ct',2,N'Bandung',N'Jawa Barat'),('ct',3,N'Surabaya',N'Jawa Timur'),('ct',4,N'Medan',N'Sumatera Utara'),
 ('ct',5,N'Semarang',N'Jawa Tengah'),('ct',6,N'Yogyakarta',N'DI Yogyakarta'),('ct',7,N'Makassar',N'Sulawesi Selatan'),('ct',8,N'Denpasar',N'Bali'),
 ('ct',9,N'Palembang',N'Sumatera Selatan'),('ct',10,N'Malang',N'Jawa Timur'),('ct',11,N'Balikpapan',N'Kalimantan Timur'),('ct',12,N'Pontianak',N'Kalimantan Barat');
GO

-- ---------- 1. Departments (12) ----------
CREATE TABLE app.Departments (
    Id int NOT NULL CONSTRAINT PK_Departments PRIMARY KEY,
    Code nvarchar(10) NOT NULL CONSTRAINT UQ_Departments_Code UNIQUE,
    Name nvarchar(80) NOT NULL,
    Location nvarchar(60) NULL,
    Budget decimal(14,2) NOT NULL,
    Created date NOT NULL
);
INSERT app.Departments (Id, Code, Name, Location, Budget, Created) VALUES
 (1,N'FIN',N'Keuangan',N'Jakarta',2500000000,'2015-01-05'),(2,N'HR',N'Sumber Daya Manusia',N'Jakarta',1200000000,'2015-01-05'),
 (3,N'IT',N'Teknologi Informasi',N'Bandung',4200000000,'2016-03-01'),(4,N'OPS',N'Operasional',N'Surabaya',3600000000,'2015-06-10'),
 (5,N'MKT',N'Pemasaran',N'Jakarta',2900000000,'2016-08-15'),(6,N'SAL',N'Penjualan',N'Medan',3100000000,'2016-08-15'),
 (7,N'LOG',N'Logistik',N'Semarang',2200000000,'2017-02-20'),(8,N'PRC',N'Pengadaan',N'Surabaya',1500000000,'2017-02-20'),
 (9,N'RND',N'Riset dan Pengembangan',N'Bandung',3300000000,'2018-01-08'),(10,N'LEG',N'Hukum',N'Jakarta',800000000,'2018-05-14'),
 (11,N'CS',N'Layanan Pelanggan',N'Yogyakarta',1400000000,'2019-04-01'),(12,N'EDU',N'Akademik',N'Malang',1900000000,'2019-09-02');

-- ---------- 2. Employees (150) ----------
CREATE TABLE app.Employees (
    Id int NOT NULL CONSTRAINT PK_Employees PRIMARY KEY,
    FirstName nvarchar(40) NOT NULL,
    LastName nvarchar(40) NOT NULL,
    Email nvarchar(120) NOT NULL CONSTRAINT UQ_Employees_Email UNIQUE,
    DepartmentId int NOT NULL CONSTRAINT FK_Employees_Departments REFERENCES app.Departments (Id),
    ManagerId int NULL CONSTRAINT FK_Employees_Manager REFERENCES app.Employees (Id),
    Title nvarchar(40) NOT NULL,
    Salary decimal(12,2) NOT NULL,
    HireDate date NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_Employees_IsActive DEFAULT 1
);
INSERT app.Employees (Id, FirstName, LastName, Email, DepartmentId, ManagerId, Title, Salary, HireDate, IsActive)
SELECT n.i, fn.v, ln.v, LOWER(fn.v) + N'.' + LOWER(ln.v) + CAST(n.i AS nvarchar(10)) + N'@contoh.id',
       1 + (n.i - 1) % 12,
       CASE WHEN n.i <= 12 THEN NULL ELSE 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'm')))) & 2147483647) % 12 END,
       CASE WHEN n.i <= 12 THEN N'Manajer' ELSE CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 't')))) & 2147483647) % 6, N'Staf', N'Analis', N'Supervisor', N'Engineer', N'Koordinator', N'Spesialis') END,
       CAST((6000000 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 's')))) & 2147483647) % 24000000) / 100000 * 100000 AS decimal(12,2)),
       DATEADD(day, -((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'h')))) & 2147483647) % 3650), CAST(SYSDATETIME() AS date)),
       CASE WHEN (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'a')))) & 2147483647) % 10 = 0 THEN 0 ELSE 1 END
FROM #n n
JOIN #w fn ON fn.kind = 'fn' AND fn.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'f')))) & 2147483647) % 20
JOIN #w ln ON ln.kind = 'ln' AND ln.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'l')))) & 2147483647) % 20
WHERE n.i <= 150;
CREATE INDEX IX_Employees_Department ON app.Employees (DepartmentId);

-- ---------- 3. Categories (15) ----------
CREATE TABLE app.Categories (
    Id int NOT NULL CONSTRAINT PK_Categories PRIMARY KEY,
    Name nvarchar(60) NOT NULL,
    ParentId int NULL CONSTRAINT FK_Categories_Parent REFERENCES app.Categories (Id)
);
INSERT app.Categories (Id, Name, ParentId) VALUES
 (1,N'Elektronik',NULL),(2,N'Perabot',NULL),(3,N'Dapur',NULL),(4,N'Fashion',NULL),(5,N'Alat Tulis',NULL),
 (6,N'Komputer',1),(7,N'Audio',1),(8,N'Kursi dan Meja',2),(9,N'Pencahayaan',2),(10,N'Peralatan Masak',3),
 (11,N'Wadah',3),(12,N'Tas dan Sepatu',4),(13,N'Pakaian',4),(14,N'Buku',5),(15,N'Perlengkapan Kantor',5);

-- ---------- 4. Suppliers (40) ----------
CREATE TABLE app.Suppliers (
    Id int NOT NULL CONSTRAINT PK_Suppliers PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Country nvarchar(40) NOT NULL,
    ContactEmail nvarchar(120) NULL,
    Rating tinyint NOT NULL,
    IsPreferred bit NOT NULL
);
INSERT app.Suppliers (Id, Name, Country, ContactEmail, Rating, IsPreferred)
SELECT n.i, N'PT ' + ln.v + N' ' + CHOOSE(1 + n.i % 5, N'Jaya', N'Makmur', N'Abadi', N'Sentosa', N'Mandiri'),
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'c')))) & 2147483647) % 6, N'Indonesia', N'Indonesia', N'Indonesia', N'Tiongkok', N'Jepang', N'Vietnam'),
       CASE WHEN n.i % 7 = 0 THEN NULL ELSE N'sales' + CAST(n.i AS nvarchar(10)) + N'@pemasok.id' END,
       1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'r')))) & 2147483647) % 5, CASE WHEN (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'p')))) & 2147483647) % 4 = 0 THEN 1 ELSE 0 END
FROM #n n JOIN #w ln ON ln.kind = 'ln' AND ln.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'x')))) & 2147483647) % 20
WHERE n.i <= 40;

-- ---------- 5. Products (200) ----------
CREATE TABLE app.Products (
    Id int NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    Sku nvarchar(20) NOT NULL CONSTRAINT UQ_Products_Sku UNIQUE,
    Name nvarchar(100) NOT NULL,
    CategoryId int NOT NULL CONSTRAINT FK_Products_Categories REFERENCES app.Categories (Id),
    SupplierId int NOT NULL CONSTRAINT FK_Products_Suppliers REFERENCES app.Suppliers (Id),
    UnitPrice decimal(12,2) NOT NULL,
    Cost decimal(12,2) NOT NULL,
    WeightKg decimal(8,3) NULL,
    IsActive bit NOT NULL,
    LaunchDate date NOT NULL
);
INSERT app.Products (Id, Sku, Name, CategoryId, SupplierId, UnitPrice, Cost, WeightKg, IsActive, LaunchDate)
SELECT n.i, N'SKU-' + RIGHT(N'0000' + CAST(n.i AS nvarchar(10)), 4), aj.v + N' ' + nn.v + N' ' + CAST(100 + n.i AS nvarchar(10)),
       1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'cat')))) & 2147483647) % 15, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sup')))) & 2147483647) % 40,
       p.price, CAST(ROUND(p.price * (0.55 + ((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'cost')))) & 2147483647) % 25) / 100.0), -2) AS decimal(12,2)),
       CASE WHEN n.i % 9 = 0 THEN NULL ELSE CAST((50 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'wt')))) & 2147483647) % 9000) / 1000.0 AS decimal(8,3)) END,
       CASE WHEN (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'act')))) & 2147483647) % 12 = 0 THEN 0 ELSE 1 END,
       DATEADD(day, -((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ld')))) & 2147483647) % 1800), CAST(SYSDATETIME() AS date))
FROM #n n
JOIN #w aj ON aj.kind = 'aj' AND aj.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'aj')))) & 2147483647) % 10
JOIN #w nn ON nn.kind = 'nn' AND nn.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'nn')))) & 2147483647) % 20
CROSS APPLY (SELECT CAST((15000 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'pr')))) & 2147483647) % 4985000) / 500 * 500 AS decimal(12,2)) AS price) p
WHERE n.i <= 200;
CREATE INDEX IX_Products_Category ON app.Products (CategoryId);

-- ---------- 6. Warehouses (8) ----------
CREATE TABLE app.Warehouses (
    Id int NOT NULL CONSTRAINT PK_Warehouses PRIMARY KEY,
    Code nvarchar(10) NOT NULL CONSTRAINT UQ_Warehouses_Code UNIQUE,
    City nvarchar(40) NOT NULL,
    CapacityUnits int NOT NULL
);
INSERT app.Warehouses (Id, Code, City, CapacityUnits)
SELECT n.i, N'WH-' + RIGHT(N'0' + CAST(n.i AS nvarchar(5)), 2), c.v, 5000 + (n.i * 1700) % 15000
FROM #n n JOIN #w c ON c.kind = 'ct' AND c.id = n.i WHERE n.i <= 8;

-- ---------- 7. Stock (~400) ----------
CREATE TABLE app.Stock (
    ProductId int NOT NULL CONSTRAINT FK_Stock_Products REFERENCES app.Products (Id),
    WarehouseId int NOT NULL CONSTRAINT FK_Stock_Warehouses REFERENCES app.Warehouses (Id),
    Quantity int NOT NULL,
    ReorderLevel int NOT NULL,
    LastCounted datetime2 NOT NULL,
    CONSTRAINT PK_Stock PRIMARY KEY (ProductId, WarehouseId)
);
INSERT app.Stock (ProductId, WarehouseId, Quantity, ReorderLevel, LastCounted)
SELECT p.Id, w.Id, (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(p.Id, '|', w.Id, '|', 'q')))) & 2147483647) % 500, 20 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(p.Id, '|', 'rl')))) & 2147483647) % 60,
       DATEADD(minute, -((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(p.Id, '|', w.Id, '|', 'lc')))) & 2147483647) % 100000), SYSDATETIME())
FROM app.Products p CROSS JOIN app.Warehouses w
WHERE (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(p.Id, '|', w.Id, '|', 'pick')))) & 2147483647) % 4 = 0;

-- ---------- 8. Stores (30) ----------
CREATE TABLE app.Stores (
    Id int NOT NULL CONSTRAINT PK_Stores PRIMARY KEY,
    Name nvarchar(80) NOT NULL,
    City nvarchar(40) NOT NULL,
    Province nvarchar(40) NOT NULL,
    OpenedOn date NOT NULL,
    AreaSqm int NOT NULL,
    ManagerId int NULL CONSTRAINT FK_Stores_Manager REFERENCES app.Employees (Id)
);
INSERT app.Stores (Id, Name, City, Province, OpenedOn, AreaSqm, ManagerId)
SELECT n.i, N'Toko ' + c.v + N' ' + CHOOSE(1 + n.i % 4, N'Pusat', N'Timur', N'Barat', N'Selatan'), c.v, c.v2,
       DATEADD(day, -(200 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'op')))) & 2147483647) % 3000), CAST(SYSDATETIME() AS date)), 40 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sq')))) & 2147483647) % 460,
       CASE WHEN n.i % 8 = 0 THEN NULL ELSE 13 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'mg')))) & 2147483647) % 130 END
FROM #n n JOIN #w c ON c.kind = 'ct' AND c.id = 1 + (n.i - 1) % 12 WHERE n.i <= 30;

-- ---------- 9. Buyers (200) ----------
CREATE TABLE app.Buyers (
    Id int NOT NULL CONSTRAINT PK_Buyers PRIMARY KEY,
    PublicId uniqueidentifier NOT NULL CONSTRAINT DF_Buyers_PublicId DEFAULT NEWID(),
    FullName nvarchar(100) NOT NULL,
    Email nvarchar(120) NULL,
    City nvarchar(40) NOT NULL,
    Segment nvarchar(20) NOT NULL,
    Joined date NOT NULL,
    CreditLimit decimal(14,2) NOT NULL,
    Notes nvarchar(max) NULL
);
INSERT app.Buyers (Id, FullName, Email, City, Segment, Joined, CreditLimit, Notes)
SELECT n.i, fn.v + N' ' + ln.v, CASE WHEN n.i % 11 = 0 THEN NULL ELSE LOWER(fn.v) + CAST(n.i AS nvarchar(10)) + N'@surel.id' END, c.v,
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sg')))) & 2147483647) % 4, N'Ritel', N'Grosir', N'Korporat', N'Pemerintah'),
       DATEADD(day, -((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'jn')))) & 2147483647) % 2500), CAST(SYSDATETIME() AS date)),
       CAST((1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'cl')))) & 2147483647) % 40) * 5000000 AS decimal(14,2)),
       CASE WHEN n.i % 6 = 0 THEN N'Pelanggan prioritas. ' + REPLICATE(N'Catatan panjang untuk uji kolom nvarchar(max). ', 1 + n.i % 4) END
FROM #n n
JOIN #w fn ON fn.kind = 'fn' AND fn.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'bf')))) & 2147483647) % 20
JOIN #w ln ON ln.kind = 'ln' AND ln.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'bl')))) & 2147483647) % 20
JOIN #w c ON c.kind = 'ct' AND c.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'bc')))) & 2147483647) % 12
WHERE n.i <= 200;

-- ---------- 10. Orders (500) ----------
CREATE TABLE app.Orders (
    Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    BuyerId int NOT NULL CONSTRAINT FK_Orders_Buyers REFERENCES app.Buyers (Id),
    StoreId int NOT NULL CONSTRAINT FK_Orders_Stores REFERENCES app.Stores (Id),
    OrderDate datetime2 NOT NULL,
    Status nvarchar(20) NOT NULL,
    Channel nvarchar(20) NOT NULL,
    ShippingFee decimal(10,2) NOT NULL,
    Note nvarchar(200) NULL
);
INSERT app.Orders (Id, BuyerId, StoreId, OrderDate, Status, Channel, ShippingFee, Note)
SELECT n.i, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ob')))) & 2147483647) % 200, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'os')))) & 2147483647) % 30,
       DATEADD(minute, (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'om')))) & 2147483647) % 1440, CAST(DATEADD(day, -((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'od')))) & 2147483647) % 400), CAST(SYSDATETIME() AS date)) AS datetime2)),
       CASE WHEN h.v < 10 THEN N'Baru' WHEN h.v < 25 THEN N'Diproses' WHEN h.v < 40 THEN N'Dikirim' WHEN h.v < 90 THEN N'Selesai' ELSE N'Batal' END,
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ch')))) & 2147483647) % 4, N'Toko', N'Web', N'Aplikasi', N'Marketplace'),
       CAST(((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sf')))) & 2147483647) % 20) * 2500 AS decimal(10,2)),
       CASE WHEN n.i % 13 = 0 THEN N'Kirim sebelum jam 5 sore' WHEN n.i % 29 = 0 THEN N'Hadiah, mohon dibungkus' END
FROM #n n CROSS APPLY (SELECT (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ost')))) & 2147483647) % 100 AS v) h WHERE n.i <= 500;
CREATE INDEX IX_Orders_Buyer ON app.Orders (BuyerId);
CREATE INDEX IX_Orders_Date ON app.Orders (OrderDate);

-- ---------- 11. OrderItems (~1500) ----------
CREATE TABLE app.OrderItems (
    Id int NOT NULL CONSTRAINT PK_OrderItems PRIMARY KEY,
    OrderId int NOT NULL CONSTRAINT FK_OrderItems_Orders REFERENCES app.Orders (Id),
    ProductId int NOT NULL CONSTRAINT FK_OrderItems_Products REFERENCES app.Products (Id),
    Qty int NOT NULL,
    UnitPrice decimal(12,2) NOT NULL,
    Discount decimal(5,2) NOT NULL
);
INSERT app.OrderItems (Id, OrderId, ProductId, Qty, UnitPrice, Discount)
SELECT ROW_NUMBER() OVER (ORDER BY o.Id, k.i), o.Id, p.Id, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(o.Id, '|', k.i, '|', 'qty')))) & 2147483647) % 6, p.UnitPrice,
       CASE WHEN (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(o.Id, '|', k.i, '|', 'dc')))) & 2147483647) % 5 = 0 THEN CAST(5 * (1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(o.Id, '|', k.i, '|', 'dv')))) & 2147483647) % 5) AS decimal(5,2)) ELSE 0 END
FROM app.Orders o
JOIN #n k ON k.i <= 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(o.Id, '|', 'cnt')))) & 2147483647) % 5
JOIN app.Products p ON p.Id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(o.Id, '|', k.i, '|', 'prod')))) & 2147483647) % 200;
CREATE INDEX IX_OrderItems_Order ON app.OrderItems (OrderId);

-- ---------- 12. Invoices ----------
CREATE TABLE app.Invoices (
    Id int NOT NULL CONSTRAINT PK_Invoices PRIMARY KEY,
    OrderId int NOT NULL CONSTRAINT UQ_Invoices_Order UNIQUE CONSTRAINT FK_Invoices_Orders REFERENCES app.Orders (Id),
    InvoiceNo nvarchar(20) NOT NULL CONSTRAINT UQ_Invoices_No UNIQUE,
    IssuedOn date NOT NULL,
    DueOn date NOT NULL,
    Total decimal(14,2) NOT NULL,
    Status nvarchar(20) NOT NULL
);
INSERT app.Invoices (Id, OrderId, InvoiceNo, IssuedOn, DueOn, Total, Status)
SELECT ROW_NUMBER() OVER (ORDER BY o.Id), o.Id, N'INV-' + CAST(YEAR(o.OrderDate) AS nvarchar(4)) + N'-' + RIGHT(N'00000' + CAST(o.Id AS nvarchar(10)), 5),
       CAST(o.OrderDate AS date), DATEADD(day, 14, CAST(o.OrderDate AS date)),
       CAST(ROUND(t.Sub + o.ShippingFee, 0) AS decimal(14,2)),
       CASE WHEN (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(o.Id, '|', 'inv')))) & 2147483647) % 100 < 70 THEN N'Lunas'
            WHEN DATEADD(day, 14, CAST(o.OrderDate AS date)) >= CAST(SYSDATETIME() AS date) THEN N'Terbuka'
            WHEN (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(o.Id, '|', 'inv')))) & 2147483647) % 100 < 90 THEN N'Terbuka' ELSE N'Terlambat' END
FROM app.Orders o
CROSS APPLY (SELECT SUM(i.Qty * i.UnitPrice * (1 - i.Discount / 100.0)) AS Sub FROM app.OrderItems i WHERE i.OrderId = o.Id) t
WHERE o.Status <> N'Batal';

-- ---------- 13. Payments ----------
CREATE TABLE app.Payments (
    Id int NOT NULL CONSTRAINT PK_Payments PRIMARY KEY,
    InvoiceId int NOT NULL CONSTRAINT FK_Payments_Invoices REFERENCES app.Invoices (Id),
    PaidOn datetime2 NOT NULL,
    Amount decimal(14,2) NOT NULL,
    Method nvarchar(20) NOT NULL,
    Reference nvarchar(40) NULL
);
INSERT app.Payments (Id, InvoiceId, PaidOn, Amount, Method, Reference)
SELECT ROW_NUMBER() OVER (ORDER BY v.Id), v.Id,
       DATEADD(hour, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(v.Id, '|', 'pd')))) & 2147483647) % 300, CAST(v.IssuedOn AS datetime2)),
       CASE WHEN v.Status = N'Lunas' THEN v.Total ELSE CAST(ROUND(v.Total / 2, 0) AS decimal(14,2)) END,
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(v.Id, '|', 'pm')))) & 2147483647) % 4, N'Transfer', N'Kartu', N'Tunai', N'QRIS'),
       CASE WHEN (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(v.Id, '|', 'pr')))) & 2147483647) % 5 = 0 THEN NULL ELSE N'REF' + CAST(100000 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(v.Id, '|', 'rf')))) & 2147483647) % 900000 AS nvarchar(10)) END
FROM app.Invoices v
WHERE v.Status = N'Lunas' OR (v.Status = N'Terbuka' AND (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(v.Id, '|', 'part')))) & 2147483647) % 3 = 0);

-- ---------- 14. Projects (25) ----------
CREATE TABLE app.Projects (
    Id int NOT NULL CONSTRAINT PK_Projects PRIMARY KEY,
    Code nvarchar(12) NOT NULL CONSTRAINT UQ_Projects_Code UNIQUE,
    Name nvarchar(100) NOT NULL,
    DepartmentId int NOT NULL CONSTRAINT FK_Projects_Departments REFERENCES app.Departments (Id),
    OwnerId int NOT NULL CONSTRAINT FK_Projects_Owner REFERENCES app.Employees (Id),
    StartDate date NOT NULL,
    EndDate date NULL,
    Budget decimal(14,2) NOT NULL,
    Status nvarchar(20) NOT NULL
);
INSERT app.Projects (Id, Code, Name, DepartmentId, OwnerId, StartDate, EndDate, Budget, Status)
SELECT n.i, N'PRJ-' + RIGHT(N'000' + CAST(n.i AS nvarchar(5)), 3),
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'pn')))) & 2147483647) % 8, N'Migrasi', N'Otomasi', N'Pembaruan', N'Integrasi', N'Audit', N'Ekspansi', N'Pelatihan', N'Optimasi')
         + N' ' + CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'pm')))) & 2147483647) % 6, N'Gudang', N'Penjualan', N'Keuangan', N'Layanan', N'Data', N'Jaringan'),
       1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'pd')))) & 2147483647) % 12, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'po')))) & 2147483647) % 150,
       DATEADD(day, -(30 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ps')))) & 2147483647) % 700), CAST(SYSDATETIME() AS date)),
       CASE WHEN n.i % 3 = 0 THEN NULL ELSE DATEADD(day, 60 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'pe')))) & 2147483647) % 300, CAST(SYSDATETIME() AS date)) END,
       CAST((1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'pb')))) & 2147483647) % 50) * CAST(50000000 AS bigint) AS decimal(14,2)),
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'pst')))) & 2147483647) % 4, N'Rencana', N'Berjalan', N'Berjalan', N'Selesai')
FROM #n n WHERE n.i <= 25;

-- ---------- 15. Tasks (300) ----------
CREATE TABLE app.Tasks (
    Id int NOT NULL CONSTRAINT PK_Tasks PRIMARY KEY,
    ProjectId int NOT NULL CONSTRAINT FK_Tasks_Projects REFERENCES app.Projects (Id),
    Title nvarchar(120) NOT NULL,
    AssigneeId int NULL CONSTRAINT FK_Tasks_Assignee REFERENCES app.Employees (Id),
    Priority tinyint NOT NULL,
    Status nvarchar(20) NOT NULL,
    DueDate date NULL,
    EstimateHours decimal(5,1) NOT NULL,
    IsDone bit NOT NULL
);
INSERT app.Tasks (Id, ProjectId, Title, AssigneeId, Priority, Status, DueDate, EstimateHours, IsDone)
SELECT n.i, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'tp')))) & 2147483647) % 25,
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'tv')))) & 2147483647) % 8, N'Analisis', N'Desain', N'Implementasi', N'Uji', N'Dokumentasi', N'Review', N'Rilis', N'Pemantauan')
         + N' ' + CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'tw')))) & 2147483647) % 6, N'modul A', N'modul B', N'antarmuka', N'basis data', N'laporan', N'integrasi'),
       CASE WHEN n.i % 10 = 0 THEN NULL ELSE 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ta')))) & 2147483647) % 150 END, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'tr')))) & 2147483647) % 5,
       s.v, CASE WHEN n.i % 12 = 0 THEN NULL ELSE DATEADD(day, (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'td')))) & 2147483647) % 200 - 100, CAST(SYSDATETIME() AS date)) END,
       CAST((5 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'te')))) & 2147483647) % 395) / 10.0 AS decimal(5,1)), CASE WHEN s.v = N'Selesai' THEN 1 ELSE 0 END
FROM #n n CROSS APPLY (SELECT CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'tst')))) & 2147483647) % 4, N'Belum', N'Dikerjakan', N'Review', N'Selesai') AS v) s WHERE n.i <= 300;
CREATE INDEX IX_Tasks_Project ON app.Tasks (ProjectId);

-- ---------- 16. Tickets (250) ----------
CREATE TABLE app.Tickets (
    Id int NOT NULL CONSTRAINT PK_Tickets PRIMARY KEY,
    Subject nvarchar(150) NOT NULL,
    RequesterId int NOT NULL CONSTRAINT FK_Tickets_Requester REFERENCES app.Employees (Id),
    AssigneeId int NULL CONSTRAINT FK_Tickets_Assignee REFERENCES app.Employees (Id),
    Priority nvarchar(10) NOT NULL,
    Status nvarchar(20) NOT NULL,
    Category nvarchar(30) NOT NULL,
    Opened datetime2 NOT NULL,
    Closed datetime2 NULL
);
INSERT app.Tickets (Id, Subject, RequesterId, AssigneeId, Priority, Status, Category, Opened, Closed)
SELECT n.i, CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ks')))) & 2147483647) % 8, N'Tidak bisa login', N'Printer macet', N'Permintaan akses', N'Laptop lambat', N'Email tidak masuk',
                   N'VPN putus', N'Aplikasi error', N'Permintaan perangkat baru') + N' #' + CAST(n.i AS nvarchar(10)),
       1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'kr')))) & 2147483647) % 150, CASE WHEN st.v = N'Baru' THEN NULL ELSE 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ka')))) & 2147483647) % 150 END,
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'kp')))) & 2147483647) % 4, N'Rendah', N'Sedang', N'Tinggi', N'Kritis'), st.v,
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'kc')))) & 2147483647) % 4, N'Akun', N'Perangkat', N'Jaringan', N'Aplikasi'), o.v,
       CASE WHEN st.v IN (N'Selesai', N'Ditutup') THEN DATEADD(hour, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'kh')))) & 2147483647) % 200, o.v) END
FROM #n n
CROSS APPLY (SELECT CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'kst')))) & 2147483647) % 5, N'Baru', N'Dikerjakan', N'Menunggu', N'Selesai', N'Ditutup') AS v) st
CROSS APPLY (SELECT DATEADD(minute, -((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ko')))) & 2147483647) % 400000), SYSDATETIME()) AS v) o
WHERE n.i <= 250;

-- ---------- 17. Courses (30) ----------
CREATE TABLE app.Courses (
    Id int NOT NULL CONSTRAINT PK_Courses PRIMARY KEY,
    Code nvarchar(10) NOT NULL CONSTRAINT UQ_Courses_Code UNIQUE,
    Title nvarchar(100) NOT NULL,
    Credits tinyint NOT NULL,
    DepartmentId int NOT NULL CONSTRAINT FK_Courses_Departments REFERENCES app.Departments (Id),
    Level nvarchar(10) NOT NULL
);
INSERT app.Courses (Id, Code, Title, Credits, DepartmentId, Level)
SELECT n.i, N'CRS' + RIGHT(N'000' + CAST(100 + n.i AS nvarchar(5)), 3),
       CHOOSE(1 + (n.i - 1) % 10, N'Pengantar', N'Dasar', N'Lanjutan', N'Praktikum', N'Studi Kasus', N'Metode', N'Analisis', N'Perancangan', N'Manajemen', N'Seminar')
         + N' ' + CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'cn')))) & 2147483647) % 6, N'Pemrograman', N'Basis Data', N'Statistika', N'Akuntansi', N'Jaringan', N'Pemasaran'),
       2 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'cc')))) & 2147483647) % 3, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'cd')))) & 2147483647) % 12, CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'cl')))) & 2147483647) % 3, N'Dasar', N'Menengah', N'Lanjut')
FROM #n n WHERE n.i <= 30;

-- ---------- 18. Students (180) ----------
CREATE TABLE app.Students (
    Id int NOT NULL CONSTRAINT PK_Students PRIMARY KEY,
    StudentNo nvarchar(12) NOT NULL CONSTRAINT UQ_Students_No UNIQUE,
    FullName nvarchar(100) NOT NULL,
    Email nvarchar(120) NULL,
    BirthDate date NOT NULL,
    EnrollmentYear int NOT NULL,
    Gpa decimal(3,2) NULL,
    City nvarchar(40) NOT NULL
);
INSERT app.Students (Id, StudentNo, FullName, Email, BirthDate, EnrollmentYear, Gpa, City)
SELECT n.i, CAST(y.v AS nvarchar(4)) + RIGHT(N'0000' + CAST(n.i AS nvarchar(6)), 4), fn.v + N' ' + ln.v,
       CASE WHEN n.i % 17 = 0 THEN NULL ELSE LOWER(fn.v) + CAST(n.i AS nvarchar(10)) + N'@kampus.ac.id' END,
       DATEADD(day, -(6500 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sb')))) & 2147483647) % 2500), CAST(SYSDATETIME() AS date)), y.v,
       CASE WHEN y.v >= YEAR(SYSDATETIME()) THEN NULL ELSE CAST(2.0 + ((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sg')))) & 2147483647) % 200) / 100.0 AS decimal(3,2)) END, c.v
FROM #n n
CROSS APPLY (SELECT YEAR(SYSDATETIME()) - (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sy')))) & 2147483647) % 5 AS v) y
JOIN #w fn ON fn.kind = 'fn' AND fn.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sf')))) & 2147483647) % 20
JOIN #w ln ON ln.kind = 'ln' AND ln.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sl')))) & 2147483647) % 20
JOIN #w c ON c.kind = 'ct' AND c.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'sc')))) & 2147483647) % 12
WHERE n.i <= 180;

-- ---------- 19. Enrollments (~600) ----------
CREATE TABLE app.Enrollments (
    Id int NOT NULL CONSTRAINT PK_Enrollments PRIMARY KEY,
    StudentId int NOT NULL CONSTRAINT FK_Enrollments_Students REFERENCES app.Students (Id),
    CourseId int NOT NULL CONSTRAINT FK_Enrollments_Courses REFERENCES app.Courses (Id),
    Semester nvarchar(10) NOT NULL,
    Score decimal(5,2) NULL,
    Grade nvarchar(2) NULL,
    CONSTRAINT UQ_Enrollments UNIQUE (StudentId, CourseId)
);
INSERT app.Enrollments (Id, StudentId, CourseId, Semester, Score, Grade)
SELECT ROW_NUMBER() OVER (ORDER BY x.StudentId, x.CourseId), x.StudentId, x.CourseId, x.Semester, x.Score,
       CASE WHEN x.Score IS NULL THEN NULL WHEN x.Score >= 85 THEN N'A' WHEN x.Score >= 70 THEN N'B' WHEN x.Score >= 55 THEN N'C' WHEN x.Score >= 40 THEN N'D' ELSE N'E' END
FROM (
    SELECT s.Id AS StudentId, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(s.Id, '|', k.i, '|', 'ec')))) & 2147483647) % 30 AS CourseId,
           MIN(CAST(s.EnrollmentYear + (k.i - 1) / 2 AS nvarchar(4)) + CASE WHEN k.i % 2 = 1 THEN N'-1' ELSE N'-2' END) AS Semester,
           MIN(CASE WHEN (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(s.Id, '|', k.i, '|', 'en')))) & 2147483647) % 9 = 0 THEN NULL ELSE CAST(30 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(s.Id, '|', k.i, '|', 'es')))) & 2147483647) % 7100 / 100.0 AS decimal(5,2)) END) AS Score
    FROM app.Students s JOIN #n k ON k.i <= 3 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(s.Id, '|', 'ek')))) & 2147483647) % 3
    GROUP BY s.Id, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(s.Id, '|', k.i, '|', 'ec')))) & 2147483647) % 30
) x;

-- ---------- 20. Books (120) ----------
CREATE TABLE app.Books (
    Id int NOT NULL CONSTRAINT PK_Books PRIMARY KEY,
    Isbn nvarchar(20) NOT NULL CONSTRAINT UQ_Books_Isbn UNIQUE,
    Title nvarchar(150) NOT NULL,
    Author nvarchar(80) NOT NULL,
    Genre nvarchar(30) NOT NULL,
    PublishedYear int NOT NULL,
    Pages int NOT NULL,
    CopiesTotal int NOT NULL,
    CopiesAvailable int NOT NULL
);
INSERT app.Books (Id, Isbn, Title, Author, Genre, PublishedYear, Pages, CopiesTotal, CopiesAvailable)
SELECT n.i, N'978-602-' + RIGHT(N'00000' + CAST(1000 + n.i * 7 AS nvarchar(8)), 5) + N'-' + CAST(n.i % 10 AS nvarchar(2)),
       CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'bt')))) & 2147483647) % 10, N'Belajar', N'Panduan', N'Rahasia', N'Kisah', N'Dunia', N'Seni', N'Sejarah', N'Teknik', N'Mengenal', N'Petualangan')
         + N' ' + CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'bu')))) & 2147483647) % 10, N'Algoritma', N'Nusantara', N'Kopi', N'Laut', N'Bintang', N'Hutan', N'Kota', N'Angka', N'Bahasa', N'Musik'),
       fn.v + N' ' + ln.v, CHOOSE(1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'bg')))) & 2147483647) % 6, N'Fiksi', N'Sains', N'Sejarah', N'Teknologi', N'Biografi', N'Anak'),
       1980 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'by')))) & 2147483647) % 45, 80 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'bp')))) & 2147483647) % 700, c.v, (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ba')))) & 2147483647) % (c.v + 1)
FROM #n n
JOIN #w fn ON fn.kind = 'fn' AND fn.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'baf')))) & 2147483647) % 20
JOIN #w ln ON ln.kind = 'ln' AND ln.id = 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'bal')))) & 2147483647) % 20
CROSS APPLY (SELECT 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'bc')))) & 2147483647) % 6 AS v) c
WHERE n.i <= 120;

-- ---------- 21. Loans (300) ----------
CREATE TABLE app.Loans (
    Id int NOT NULL CONSTRAINT PK_Loans PRIMARY KEY,
    BookId int NOT NULL CONSTRAINT FK_Loans_Books REFERENCES app.Books (Id),
    StudentId int NOT NULL CONSTRAINT FK_Loans_Students REFERENCES app.Students (Id),
    LoanedOn date NOT NULL,
    DueOn date NOT NULL,
    ReturnedOn date NULL,
    Fine decimal(10,2) NOT NULL
);
INSERT app.Loans (Id, BookId, StudentId, LoanedOn, DueOn, ReturnedOn, Fine)
SELECT n.i, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'lb')))) & 2147483647) % 120, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ls')))) & 2147483647) % 180, l.v, DATEADD(day, 14, l.v), r.v,
       CASE WHEN r.v IS NOT NULL AND r.v > DATEADD(day, 14, l.v) THEN CAST(DATEDIFF(day, DATEADD(day, 14, l.v), r.v) * 1000 AS decimal(10,2)) ELSE 0 END
FROM #n n
CROSS APPLY (SELECT DATEADD(day, -((CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'ld')))) & 2147483647) % 240), CAST(SYSDATETIME() AS date)) AS v) l
CROSS APPLY (SELECT CASE WHEN (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'lr')))) & 2147483647) % 4 = 0 THEN NULL
                         WHEN DATEADD(day, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'lx')))) & 2147483647) % 24, l.v) > CAST(SYSDATETIME() AS date) THEN NULL
                         ELSE DATEADD(day, 1 + (CONVERT(int, CONVERT(binary(4), HASHBYTES('MD5', CONCAT(n.i, '|', 'lx')))) & 2147483647) % 24, l.v) END AS v) r
WHERE n.i <= 300;
GO

-- ---------- views ----------
CREATE VIEW app.vw_EmployeeDirectory AS
SELECT e.Id, e.FirstName + N' ' + e.LastName AS FullName, e.Email, e.Title, d.Name AS Department, m.FirstName + N' ' + m.LastName AS Manager, e.Salary, e.HireDate, e.IsActive
FROM app.Employees e
JOIN app.Departments d ON d.Id = e.DepartmentId
LEFT JOIN app.Employees m ON m.Id = e.ManagerId;
GO
CREATE VIEW app.vw_OrderSummary AS
SELECT o.Id AS OrderId, o.OrderDate, o.Status, o.Channel, b.FullName AS Buyer, s.Name AS Store,
       COUNT(i.Id) AS Lines, SUM(i.Qty) AS Units, CAST(SUM(i.Qty * i.UnitPrice * (1 - i.Discount / 100.0)) AS decimal(14,2)) AS Subtotal
FROM app.Orders o
JOIN app.Buyers b ON b.Id = o.BuyerId
JOIN app.Stores s ON s.Id = o.StoreId
LEFT JOIN app.OrderItems i ON i.OrderId = o.Id
GROUP BY o.Id, o.OrderDate, o.Status, o.Channel, b.FullName, s.Name;
GO

-- ---------- report ----------
SELECT t.name AS TableName, SUM(p.rows) AS Total
FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE t.schema_id = SCHEMA_ID(N'app') GROUP BY t.name ORDER BY t.name;
GO
