# Rakit

Rakit adalah low-code platform untuk membangun aplikasi internal di atas SQL Server. Dengan Rakit, halaman aplikasi (tabel data, form, chart, dashboard) dan workflow disusun lewat Builder, tanpa perlu menulis controller, view, atau page baru untuk setiap kebutuhan.

Nama "Rakit" diambil dari kata *merakit*: aplikasi dibangun dengan merakit komponen yang sudah tersedia, bukan ditulis dari nol.

```
SQL Server database
  └─ Data Source      (table, query, atau stored procedure)
       └─ Module      (Table, Form, Chart, Widget, Page)
            └─ Menu   (page muncul di navbar dan bisa dibuka user)
                 └─ Action / button
                      └─ Workflow  (step: database, REST, email, script, form, dan lainnya)
                           └─ Tracking  (history setiap step: success, failed, waiting)
```

## Table of Contents

1. [Vision & Mission](#vision--mission)
2. [Features](#features)
3. [Tech Stack](#tech-stack)
4. [Requirements](#requirements)
5. [Getting Started](#getting-started)
6. [Core Concepts](#core-concepts)
7. [Tutorial: Your First Page](#tutorial-your-first-page)
8. [Tutorial: Your First Workflow](#tutorial-your-first-workflow)
9. [Configuration](#configuration)
10. [Project Structure](#project-structure)
11. [Architecture & Security](#architecture--security)
12. [Testing](#testing)
13. [Project Status](#project-status)
14. [Troubleshooting](#troubleshooting)

## Vision & Mission

### Vision

Membuat pengembangan aplikasi internal berbasis data cukup cepat sehingga sebuah kebutuhan bisnis bisa diwujudkan dalam hitungan jam, bukan minggu, tanpa mengorbankan keamanan dan keteraturan database.

### Mission

1. **Development yang lebih cepat.** Page CRUD, report, chart, dan dashboard dibangun dari konfigurasi, bukan dari kode yang ditulis berulang.
2. **SQL Server sebagai pusat.** Rakit fokus pada satu database SQL Server dan menjadikan stored procedure sebagai fitur utama yang bisa dipilih langsung dari Builder.
3. **Secure by default.** Semua nilai dikirim sebagai SQL parameter (tanpa string concatenation), stored procedure harus masuk whitelist, setiap jenis akses memakai database user dengan permission minimum, dan fitur yang berisiko (raw query, write) nonaktif secara default.
4. **Mudah dipelajari.** Alurnya konsisten: buat data source, atur tampilannya di module, pasang di menu, lalu tambahkan workflow bila perlu.
5. **Mudah ditelusuri.** Setiap step workflow tercatat, sehingga mudah melihat apa yang berjalan, apa yang gagal, dan kenapa.

## Features

| Area | Fitur |
|---|---|
| **Data Source** | Tiga mode: *Table* (pilih table atau view, kolom, filter), *Query* (SQL dengan parameter), dan *Stored Procedure* (input, output, return value, multiple result set). Nilai parameter bisa diambil dari query string, form, route, constant, atau output step workflow. Ada preview hasil. |
| **Module** | Lima tipe: **Table** (list dengan search, filter, sort, paging, add, edit, delete, detail view, CSV export dan import), **Form** (form input mandiri), **Chart** (bar, line, area, pie, doughnut), **Widget** (angka ringkasan atau KPI), dan **Page** (HTML page yang bisa meng-embed module lain, cocok untuk dashboard). |
| **Form field** | text, textarea, number, date, dateTime, boolean, email, url, select (opsi tetap atau dari data source), browse (picker dengan search), dan section (judul pemisah). Mendukung required, max length, regex, serta min dan max. |
| **Wizard "Buat dari tabel"** | Pilih satu table, lalu Rakit membuat data source List, Detail, Insert, Update, dan Delete beserta stored procedure CRUD-nya, sekaligus mengisi kolom Display dan field Form. |
| **Menu** | Navbar dua level dengan folder dan separator. Item menu bisa mengarah ke module atau URL. |
| **Workflow** | Visual canvas dengan step: Data Source, REST, Email, Page (tampilkan HTML), Module (redirect ke page lain), Condition (branching), Javascript (script di server-side sandbox), dan Form (minta input user di tengah proses). Mendukung satu SQL transaction untuk seluruh workflow. |
| **Tracking** | Summary jumlah run success, failed, dan waiting, chart per hari, serta detail setiap step dari setiap run. |
| **SQL Playground** | SQL editor dengan tab, object explorer, tampilan struktur table, dan table designer. Default-nya read-only dan selalu di-rollback. |
| **Admin** | Whitelist stored procedure dan halaman Health untuk mengecek koneksi database. |

## Tech Stack

### Backend

| Component | Version |
|---|---|
| .NET SDK | 10.0.301 |
| Target framework | `net10.0` |
| ASP.NET Core MVC (Razor Views) | 10.0 |
| Microsoft.Data.SqlClient | 7.1.1 |
| dbup-sqlserver (database migration) | 7.2.0 |
| HtmlSanitizer | 9.2.1039 |
| Jint (Javascript engine untuk step Javascript) | 4.17.0 |
| MailKit (email) | 4.18.1 |
| System.Security.Cryptography.Pkcs (di-pin karena vulnerability pada versi transitive) | 10.0.13 |

Data access memakai ADO.NET langsung, tanpa Entity Framework.

### Database

| Component | Version |
|---|---|
| Microsoft SQL Server | Minimal 2016 SP1. Dikembangkan dan dites dengan SQL Server 2025 Express (17.0.1000.7). |

### Frontend

Semua library sudah ada di `src/Web/wwwroot/lib`, jadi tidak perlu CDN atau `npm install`.

| Library | Version |
|---|---|
| Bootstrap | 5.3.3 |
| Bootstrap Icons | 1.11.3 |
| jQuery | 3.7.1 |
| jQuery Validation | 1.21.0 |
| jQuery Validation Unobtrusive | 4.0.0 |
| Chart.js | 4.4.7 |
| Ace Editor | 1.36.5 |
| Inter (font) | variable font, latin dan latin-ext |

### Testing

| Component | Version |
|---|---|
| xUnit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.4 |
| Microsoft.NET.Test.Sdk | 17.14.1 |
| coverlet.collector | 6.0.4 |

## Requirements

Sebelum menjalankan Rakit, pastikan sudah terpasang:

1. **Windows 10/11** atau Windows Server. Config default memakai Windows Authentication ke SQL Server.
2. **.NET SDK 10.0** (10.0.301 atau lebih baru). Download di [dotnet.microsoft.com](https://dotnet.microsoft.com/download). Cek dengan:
   ```
   dotnet --version
   ```
3. **SQL Server 2016 SP1 atau lebih baru.** Edisi Express sudah cukup. Config default mengarah ke instance `.\SQLEXPRESS`.
4. **Git**, untuk clone repository.
5. **sqlcmd** (opsional), untuk menjalankan sample data tambahan.
6. Akun Windows yang dipakai harus bisa **create database** di instance tersebut (misalnya punya role `sysadmin` atau `dbcreator`). Pada SQL Server Express yang di-install sendiri, akun yang meng-install biasanya sudah punya permission ini.

## Getting Started

### 1. Clone repository

```
git clone https://github.com/edrzvena/NetMvcRakit.git
cd NetMvcRakit
```

### 2. Restore dan build

```
dotnet restore Rakit.slnx
dotnet build Rakit.slnx
```

### 3. Jalankan database migration

Migration akan membuat database `Rakit` kalau belum ada, lalu membuat schema, table metadata, database user, dan sample data.

```
dotnet run --project db/Rakit.Migrations
```

Kalau SQL Server Anda bukan `.\SQLEXPRESS`, berikan connection string sebagai argument:

```
dotnet run --project db/Rakit.Migrations "Server=NAMA_SERVER;Database=Rakit;Integrated Security=true;TrustServerCertificate=true"
```

Migration aman dijalankan berulang kali. Script yang sudah dijalankan tercatat di table `dbo.SchemaVersions` dan tidak akan dijalankan ulang.

### 4. (Opsional) Sample data tambahan

File `db/Samples/sample-data.sql` berisi 21 table dan 2 view contoh (karyawan, produk, order, project, perpustakaan, dan lainnya) di schema `app`. File ini bukan migration dan aman dijalankan ulang.

```
sqlcmd -S .\SQLEXPRESS -d Rakit -C -I -E -i db\Samples\sample-data.sql
```

### 5. Jalankan aplikasi

```
dotnet run --project src/Web
```

Buka **http://localhost:5247** di browser.

Command di atas otomatis memakai environment `Development` (diatur di `src/Web/Properties/launchSettings.json`). Connection string default ada di `src/Web/appsettings.Development.json`. Kalau nama SQL Server Anda berbeda, ubah kedua value di file tersebut (`Metadata` dan `Execution`).

### 6. Aktifkan write (untuk mencoba add, edit, delete)

Default-nya Rakit berjalan dalam mode aman: data hanya bisa dibaca. Untuk mencoba fitur write di komputer development, set dua environment variable berikut sebelum menjalankan aplikasi.

PowerShell:

```powershell
$env:Rakit__Db__AllowWrite = "true"
$env:Rakit__Playground__AllowWrite = "true"
dotnet run --project src/Web
```

Command Prompt:

```
set Rakit__Db__AllowWrite=true
set Rakit__Playground__AllowWrite=true
dotnet run --project src/Web
```

Git Bash:

```bash
Rakit__Db__AllowWrite=true Rakit__Playground__AllowWrite=true dotnet run --project src/Web
```

| Environment variable | Fungsi |
|---|---|
| `Rakit__Db__AllowWrite` | Mengizinkan data source dengan statement *Command* (add, edit, delete dari page dan workflow). |
| `Rakit__Playground__AllowWrite` | Mengizinkan Playground melakukan commit dan mengizinkan wizard "Buat dari tabel" membuat stored procedure. |

Jangan aktifkan keduanya di server yang bisa diakses orang lain.

## Core Concepts

Sebelum mulai, pahami dulu istilah-istilah berikut. Setiap istilah adalah satu layer di Rakit, dan setiap layer dibangun di atas layer sebelumnya.

| Term | Penjelasan | Contoh |
|---|---|---|
| **Schema `app`** | Tempat data bisnis Anda (table, view, stored procedure). | `app.Customers` |
| **Schema `rakit`** | Tempat Rakit menyimpan metadata-nya sendiri. Jangan diubah manual. | `rakit.Modules` |
| **Data Source** | Definisi cara mengambil atau mengubah data: dari table mana, query apa, atau stored procedure apa, dan dari mana value parameternya. | "List customer", "Insert customer" |
| **Statement Query / Command** | *Query* hanya membaca data. *Command* mengubah data dan butuh `AllowWrite`. | List = Query, Insert = Command |
| **Module** | Bentuk tampilan dari sebuah data source. | Table customer, form registrasi, chart penjualan |
| **Draft / Published** | Module dan workflow baru selalu berstatus *Draft* dan belum bisa dibuka user. Setelah di-*Publish*, baru aktif. | |
| **Menu** | Link di navbar yang mengarah ke module atau URL. | Menu "Pelanggan" |
| **Workflow** | Rangkaian step yang dijalankan saat user menekan sebuah button. | Simpan order, lalu kirim email konfirmasi |
| **Placeholder** | Penanda `{{...}}` untuk menyisipkan value ke dalam teks step workflow. | `{{form.Email}}`, `{{key}}`, `{{simpan.row.Id}}` |
| **Tracking** | History setiap kali workflow dijalankan. | "Run 22 Okt failed di step kirim-email" |
| **SP Whitelist** | Daftar stored procedure yang boleh dipakai. Stored procedure di luar daftar ini selalu ditolak. | `app.SpCustomers_Insert` |

Singkatnya, alur kerja di Rakit selalu sama:

1. Siapkan **data** di schema `app`.
2. Buat **Data Source** untuk membaca atau mengubah data tersebut.
3. Buat **Module** untuk menampilkannya.
4. **Publish** module tersebut.
5. Pasang di **Menu** supaya mudah dibuka.
6. Kalau butuh proses tambahan, buat **Workflow** dan pasang sebagai button di module.

## Tutorial: Your First Page

Tutorial ini membuat page list customer lengkap dengan add, edit, dan delete, memakai table sample `app.Customers` yang dibuat oleh migration.

**Persiapan:** jalankan aplikasi dengan `AllowWrite` aktif seperti di [step 6](#6-aktifkan-write-untuk-mencoba-add-edit-delete).

### Cara cepat: wizard "Buat dari tabel"

1. Buka **http://localhost:5247**, lalu pilih **Builder > Module** di sidebar.
2. Klik **Module baru**, isi nama (misalnya `Pelanggan`), dan pilih tipe **Table**.
3. Klik **Buat dari tabel...**, lalu pilih table `app.Customers`.
4. Cek kolom yang ditampilkan dan field form yang disarankan, lalu klik **Terapkan**. Rakit akan:
   - membuat data source `customers-list`, `customers-detail`, `customers-insert`, `customers-update`, dan `customers-delete`;
   - membuat stored procedure CRUD kalau belum ada (stored procedure yang sudah ada, seperti `app.SpCustomers_Insert` dari migration, dipakai apa adanya dan tidak di-overwrite), lalu memasukkannya ke whitelist;
   - mengisi tab Display (kolom) dan tab Form (field).
5. Klik **Simpan**. Module tersimpan sebagai **Draft**.
6. Kembali ke list module, lalu klik icon **Publish** pada module `Pelanggan`.
7. Buka **Builder > Menu**, tambahkan menu baru dengan label `Pelanggan`, pilih target **Module**, lalu pilih module `Pelanggan`.
8. Menu `Pelanggan` sekarang muncul di navbar. Klik untuk membuka page-nya. URL page berbentuk `/object/page/{module-id}`.

### Cara manual: memahami setiap layer

Cara ini lebih panjang, tapi membantu memahami apa yang dikerjakan wizard di belakang layar.

1. **Buat data source untuk list.** Buka **Builder > Data Source**, buat data source baru:
   - Mode: **Table**, pilih `app.Customers`;
   - Statement: **Query**;
   - Klik **Preview** untuk memastikan data muncul, lalu simpan.
2. **Buat module.** Buka **Builder > Module**, buat module tipe **Table**:
   - Tab **Source**: pilih data source dari step 1 sebagai *List*;
   - Tab **Display**: load kolom, atur label, format, dan kolom yang bisa di-sort; isi **key field** (misalnya `Id`) kalau nanti butuh button edit atau delete;
   - Tab **Preview**: lihat hasilnya sebelum disimpan.
3. **Tambahkan write (opsional).** Buat data source dengan statement **Command** untuk Insert, Update, dan Delete (biasanya memakai stored procedure), pilih di tab Source, lalu tambahkan field di tab **Form**. Stored procedure baru harus didaftarkan dulu di **Admin > SP Whitelist**.
4. **Publish** module dan **pasang di Menu** seperti di cara cepat.

### Module type lainnya

| Type | Kapan dipakai |
|---|---|
| **Form** | Page input mandiri, misalnya form registrasi. Hanya untuk insert data. |
| **Chart** | Chart dari data source yang mengembalikan kolom label dan value. |
| **Widget** | Satu angka penting, misalnya total penjualan hari ini. |
| **Page** | HTML page bebas. Embed module lain dengan marker `{{module:MODULE-ID}}` untuk membuat dashboard. Hanya module Table, Form, Chart, dan Widget yang bisa di-embed, maksimal 10 per page. |

## Tutorial: Your First Workflow

Contoh berikut menambahkan button **Kirim Sapaan** di setiap baris table customer, yang menampilkan pesan berisi data customer tersebut.

1. Buka **Builder > Workflow**, buat workflow baru dengan code `sapa-pelanggan`.
2. Di canvas, tambahkan step **Data Source** dengan code `ambil`. Pilih data source detail customer (`customers-detail`). Parameter `id` otomatis terisi dari key baris yang diklik.
3. Tambahkan step **Page** dengan isi HTML berikut, lalu sambungkan dari step `ambil`:
   ```html
   <h5>Halo, {{ambil.row.Name}}</h5>
   <p>Email terdaftar: {{ambil.row.Email}}</p>
   ```
4. Klik **Periksa** untuk validasi, simpan, lalu **Publish**.
5. Buka module `Pelanggan`, tambahkan button di bagian **Aksi**:
   - Code: `sapa`, Label: `Kirim Sapaan`;
   - Workflow: `sapa-pelanggan`;
   - Centang **per baris**.
6. Simpan dan buka page customer. Setiap baris sekarang punya button **Kirim Sapaan**.
7. Buka **Builder > Tracking** untuk melihat history run beserta status setiap step.

Nama kolom pada `{{ambil.row.Name}}` dan `{{ambil.row.Email}}` mengikuti kolom yang dikembalikan data source. Sesuaikan kalau table Anda berbeda.

### Workflow step types

| Step | Fungsi | Output untuk step berikutnya |
|---|---|---|
| **Data Source** | Menjalankan data source (query atau stored procedure). | `{{code.row.Column}}`, `{{code.out.Parameter}}`, `{{code.return}}` |
| **REST** | Memanggil API eksternal. Host tujuan harus terdaftar di `Rakit:Workflow:AllowedHosts`. | `{{code.status}}`, `{{code.body}}`, `{{code.json.path.to.value}}` |
| **Email** | Mengirim email lewat SMTP. Kalau SMTP belum di-config, email hanya dicatat di log. | `{{code.sent}}` |
| **Page** | Menampilkan HTML ke user di modal. | |
| **Module** | Redirect user ke page module lain, bisa membawa parameter. | |
| **Condition** | Memilih branch *true* atau *false* berdasarkan perbandingan value. | |
| **Javascript** | Menjalankan script di server-side sandbox (dibatasi timeout, memory, dan jumlah statement). | `{{code.body}}`, `{{code.json.path}}` |
| **Form** | Menghentikan sementara workflow dan meminta user mengisi form. Berlaku 30 menit. | `{{code.button}}`, `{{code.form.FieldName}}` |

Placeholder yang selalu tersedia: `{{form.FieldName}}` (value form), `{{key}}` (key baris), `{{claim.name}}`, dan `{{now}}` (waktu saat ini).

## Configuration

Semua config ada di `src/Web/appsettings.json` dan `src/Web/appsettings.Development.json`. Setiap key juga bisa di-override lewat environment variable dengan mengganti `:` menjadi `__` (dua underscore), misalnya `Rakit:Db:AllowWrite` menjadi `Rakit__Db__AllowWrite`.

### Connection strings

| Key | Fungsi |
|---|---|
| `ConnectionStrings:Metadata` | Koneksi untuk membaca dan menyimpan metadata Rakit (schema `rakit`). |
| `ConnectionStrings:Execution` | Koneksi untuk menjalankan data source (schema `app`). Wajib memakai `Pooling=false`. |

### Rakit:Db

| Key | Default | Fungsi |
|---|---|---|
| `AllowedSchemas` | `["app"]` | Schema yang boleh diakses data source. |
| `AllowRawQuery` | `false` | Mengizinkan data source mode *Query* (raw SQL). |
| `AllowWrite` | `false` | Mengizinkan statement *Command* (write data). |
| `AllowStoredProcedure` | `true` | Mengizinkan data source mode *Stored Procedure*. |

### Rakit:Playground

| Key | Default | Fungsi |
|---|---|---|
| `Enabled` | `false` (`true` di Development) | Menampilkan SQL Playground. |
| `AllowWrite` | `false` | Mengizinkan Playground melakukan commit dan wizard membuat stored procedure. |
| `MaxRows` | `1000` | Jumlah row maksimal per result. |
| `TimeoutSeconds` | `30` | Timeout eksekusi script. |
| `MaxScriptLength` | `200000` | Panjang script maksimal (karakter). |

### Rakit:Workflow

| Key | Default | Fungsi |
|---|---|---|
| `AllowedHosts` | `[]` | Host yang boleh dipanggil step REST, misalnya `api.example.com` (semua port) atau `localhost:5099` (port itu saja). Kosong berarti step REST tidak bisa berjalan. |
| `Script:TimeoutMs` | `500` | Timeout step Javascript. |
| `Script:MemoryLimitMb` | `16` | Memory limit step Javascript. |
| `Script:MaxStatements` | `100000` | Jumlah statement maksimal yang dieksekusi. |
| `Script:MaxRecursion` | `100` | Kedalaman recursion maksimal. |
| `Script:MaxResultBytes` | `65536` | Ukuran result script maksimal. |

### Rakit:Mail

| Key | Default | Fungsi |
|---|---|---|
| `Host`, `Port` | kosong, `25` | SMTP server. Kalau `Host` kosong, email hanya dicatat di log. |
| `Security` | `StartTlsWhenAvailable` | Mode security koneksi SMTP. |
| `User`, `Password` | kosong | SMTP credentials. Simpan di user secrets atau environment variable, jangan di file yang di-commit. |
| `From`, `FromName` | kosong, `Rakit` | Alamat dan nama pengirim. |

### Rakit:Security

| Key | Default | Fungsi |
|---|---|---|
| `BuilderLocalOnly` | `true` | Builder dan Admin hanya bisa dibuka dari komputer tempat aplikasi berjalan. Request dari komputer lain mendapat HTTP 403. |

## Project Structure

```
NetMvcRakit/
├── Rakit.slnx                      Solution
├── db/
│   ├── Rakit.Migrations/           Migration runner (DbUp)
│   │   └── Scripts/                SQL script berurutan: 0001_schemas.sql, 0002_..., dst.
│   └── Samples/
│       └── sample-data.sql         Sample data tambahan (bukan migration)
├── src/
│   ├── Core/                       Business logic, tanpa dependency ke UI
│   │   ├── Config/                 Options class (Rakit:Db, Rakit:Playground)
│   │   ├── Data/                   Connection factory
│   │   ├── DataSources/            Data source: model, repository, execution, parameter resolver
│   │   ├── Health/                 Database health check
│   │   ├── Modules/                Module, menu, form, CSV export/import, table wizard
│   │   ├── Playground/             SQL Playground dan object catalog
│   │   ├── Procedures/             Stored procedure whitelist
│   │   ├── Schema/                 Schema browser
│   │   ├── Sql/                    Parameterized SQL execution, template, transaction
│   │   └── Workflows/              Workflow: model, validator, runner, step, tracking
│   └── Web/                        ASP.NET Core MVC app
│       ├── Areas/
│       │   ├── Admin/              SP Whitelist dan Health
│       │   ├── Builder/            Module, Workflow, Tracking, Data Source, Menu, Playground
│       │   └── Object/             Runtime page yang dibuka user (/object/page/{id})
│       ├── Security/               Pembatasan akses Builder dan security headers
│       ├── Views/                  Layout dan home page
│       └── wwwroot/                CSS, Javascript, dan frontend library
└── tests/
    └── Rakit.Core.Tests/           xUnit tests
```

## Architecture & Security

### Satu database, dua schema

Rakit memakai satu database SQL Server yang dipisah menjadi dua schema:

- **`rakit`** menyimpan metadata: data source, module, menu, workflow, tracking, dan whitelist.
- **`app`** menyimpan data bisnis beserta stored procedure-nya.

### Database user dengan least privilege

Aplikasi terhubung dengan Windows identity, lalu di setiap koneksi berpindah ke database user tanpa login (`EXECUTE AS USER ... WITH NO REVERT`):

| User | Permission |
|---|---|
| `rakit_app` | Mengelola schema `rakit` dan membaca definisi object di `app`. |
| `rakit_exec` | SELECT dan EXECUTE di schema `app`. Tidak punya akses ke schema `rakit`. |
| `rakit_play_ro` | Playground mode read-only. |
| `rakit_play` | Playground mode write (hanya kalau `Rakit:Playground:AllowWrite` aktif). |

Dengan pemisahan ini, query dari sebuah data source tidak bisa membaca atau mengubah metadata Rakit.

### Proteksi lainnya

- Semua input user dikirim sebagai SQL parameter, tidak pernah digabung ke teks SQL.
- Stored procedure wajib ada di whitelist dan dicek di setiap eksekusi.
- Data source raw query dengan statement *Query* selalu di-rollback setelah dibaca.
- Write, raw query, dan Playground write nonaktif secara default.
- Setiap aksi write memakai anti-forgery token.
- HTML di module Page di-sanitize dengan HtmlSanitizer saat disimpan dan saat di-render; HTML dari step Page dan Email di-sanitize setiap kali workflow berjalan.
- Step REST hanya boleh memanggil host yang terdaftar dan tidak mengikuti redirect.
- Step Javascript berjalan di sandbox dengan batas timeout, memory, dan jumlah statement.
- Button Tambah, Ubah, dan Hapus yang dimatikan di Builder juga ditolak di server, bukan hanya disembunyikan dari UI.
- Security headers dipasang di setiap response (`X-Content-Type-Options`, `X-Frame-Options`, `Content-Security-Policy`, `Referrer-Policy`).
- Builder dan Admin hanya bisa diakses dari localhost.

## Testing

Test membutuhkan SQL Server dan akan membuat database terpisah bernama `Rakit_Test`, jadi database `Rakit` tidak tersentuh.

```
dotnet test Rakit.slnx
```

Kalau SQL Server Anda bukan `.\SQLEXPRESS`, set environment variable `RAKIT_TEST_CONN` (tanpa nama database), misalnya:

```powershell
$env:RAKIT_TEST_CONN = "Server=NAMA_SERVER;Integrated Security=true;TrustServerCertificate=true"
dotnet test Rakit.slnx
```

## Project Status

Rakit masih dalam tahap development dan **belum siap di-deploy ke server publik**.

Sudah tersedia:

- Data source mode Table, Query, dan Stored Procedure.
- Module Table, Form, Chart, Widget, dan Page, termasuk wizard "Buat dari tabel".
- Menu dinamis dua level.
- Workflow dengan delapan step type, visual canvas, single transaction, dan tracking.
- SQL Playground dan stored procedure whitelist.

Belum tersedia:

- **Authentication dan role-based access.** Runtime page saat ini bisa dibuka siapa saja yang bisa menjangkau server. Config access di module sudah disimpan tapi belum di-enforce.
- Workflow scheduling (misalnya menjalankan workflow setiap pagi).
- Audit log perubahan config dan log viewer.
- Dukungan database selain SQL Server, dan multi database.

## Troubleshooting

| Problem | Penyebab dan solusi |
|---|---|
| Migration gagal karena tidak bisa connect ke server | Pastikan service SQL Server berjalan dan nama instance benar. Berikan connection string sebagai argument di command migration. |
| Migration gagal karena tidak bisa create database | Akun Windows Anda belum punya permission create database. Minta role `dbcreator` atau jalankan dengan akun admin SQL Server. |
| Aplikasi gagal start dengan pesan `MetadataConnectionString is required` | Connection string kosong. Pastikan aplikasi berjalan di environment `Development`, atau isi `ConnectionStrings` di `appsettings.json`. |
| Builder menampilkan HTTP 403 | Builder dibuka dari komputer lain. Buka dari komputer tempat aplikasi berjalan, atau ubah `Rakit:Security:BuilderLocalOnly` khusus untuk environment development yang tertutup. |
| Button Tambah, Ubah, atau Hapus tidak muncul | `Rakit:Db:AllowWrite` belum aktif, data source Insert/Update/Delete belum dipilih, key field belum diisi, atau button-nya dimatikan di tab Display. |
| Page module menampilkan 404 | Module masih Draft. Publish dulu dari list module. |
| Data source ditolak karena whitelist | Daftarkan stored procedure tersebut di **Admin > SP Whitelist** dan pastikan statusnya aktif. |
| Step REST selalu gagal dengan pesan "tidak tersedia" | Host tujuan belum terdaftar di `Rakit:Workflow:AllowedHosts`. |
| Email tidak terkirim | `Rakit:Mail:Host` belum diisi, jadi email hanya dicatat di log. Lengkapi config SMTP. |
| Wizard "Buat dari tabel" tidak bisa membuat stored procedure | Aktifkan `Rakit:Playground:Enabled` dan `Rakit:Playground:AllowWrite`. |
