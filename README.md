# Rakit

Rakit adalah platform low-code untuk membangun aplikasi internal di atas SQL Server. Dengan Rakit, halaman aplikasi (tabel data, form isian, grafik, dashboard) dan alur proses (workflow) disusun lewat antarmuka Builder, tanpa perlu menulis controller, view, atau halaman baru untuk setiap kebutuhan.

Nama "Rakit" diambil dari kata *merakit*: aplikasi dibangun dengan merakit komponen yang sudah tersedia, bukan ditulis dari nol.

```
Database SQL Server
  └─ Data Source      (sumber data: tabel, query, atau stored procedure)
       └─ Module      (bentuk tampilan: Table, Form, Chart, Widget, Page)
            └─ Menu   (halaman muncul di navigasi dan bisa dibuka pengguna)
                 └─ Aksi / tombol
                      └─ Workflow  (rangkaian langkah: database, REST, email, skrip, form, dan lainnya)
                           └─ Tracking  (riwayat setiap langkah: sukses, gagal, menunggu)
```

## Daftar Isi

1. [Visi dan Misi](#visi-dan-misi)
2. [Fitur Utama](#fitur-utama)
3. [Tech Stack](#tech-stack)
4. [Persyaratan Sistem](#persyaratan-sistem)
5. [Cara Menjalankan](#cara-menjalankan)
6. [Konsep Dasar untuk Pemula](#konsep-dasar-untuk-pemula)
7. [Tutorial: Membuat Halaman Pertama](#tutorial-membuat-halaman-pertama)
8. [Tutorial: Membuat Workflow Pertama](#tutorial-membuat-workflow-pertama)
9. [Konfigurasi](#konfigurasi)
10. [Struktur Proyek](#struktur-proyek)
11. [Arsitektur dan Keamanan](#arsitektur-dan-keamanan)
12. [Pengujian](#pengujian)
13. [Status Proyek dan Batasan](#status-proyek-dan-batasan)
14. [Pemecahan Masalah](#pemecahan-masalah)

## Visi dan Misi

### Visi

Menjadikan pembuatan aplikasi internal berbasis data cukup cepat sehingga sebuah kebutuhan bisnis bisa diwujudkan dalam hitungan jam, bukan minggu, tanpa mengorbankan keamanan dan keteraturan database.

### Misi

1. **Mempercepat pengembangan.** Halaman CRUD, laporan, grafik, dan dashboard dibangun dari konfigurasi, bukan dari kode yang ditulis berulang.
2. **Menjadikan SQL Server pusatnya.** Rakit fokus pada satu database SQL Server dan memperlakukan stored procedure sebagai fitur utama yang bisa dipilih langsung dari Builder.
3. **Aman sejak awal.** Semua nilai dikirim sebagai parameter (tanpa penggabungan string SQL), stored procedure harus masuk whitelist, setiap jenis akses memakai user database dengan hak minimum, dan fitur yang berisiko (query mentah, penulisan data) mati secara default.
4. **Mudah dipelajari.** Alur kerjanya konsisten: buat sumber data, bentuk tampilannya, pasang di menu, lalu tambahkan proses bila perlu.
5. **Dapat ditelusuri.** Setiap langkah workflow tercatat, sehingga mudah melihat apa yang berjalan, apa yang gagal, dan alasannya.

## Fitur Utama

| Area | Fitur |
|---|---|
| **Data Source** | Tiga mode: *Table* (pilih tabel atau view, kolom, filter), *Query* (SQL berparameter), dan *Stored Procedure* (parameter input, output, return value, beberapa result set). Parameter bisa diambil dari query string, form, route, konstanta, atau output langkah workflow. Tersedia preview hasil. |
| **Module** | Lima tipe: **Table** (daftar data dengan cari, filter, urut, paging, tambah, ubah, hapus, lihat detail, export dan import CSV), **Form** (form isian mandiri), **Chart** (grafik batang, garis, pie, dan sejenisnya), **Widget** (angka ringkasan atau KPI), dan **Page** (halaman HTML yang dapat menyematkan module lain, cocok untuk dashboard). |
| **Field form** | text, textarea, number, date, dateTime, boolean, email, url, select (pilihan tetap atau dari data source), browse (pemilih data dengan pencarian), dan section (judul pemisah). Mendukung wajib isi, panjang maksimal, regex, nilai minimum dan maksimum. |
| **Wizard "Buat dari tabel"** | Pilih satu tabel, lalu Rakit membuat data source List, Detail, Insert, Update, dan Delete beserta stored procedure CRUD-nya, sekaligus mengisi kolom tampilan dan field form. |
| **Menu** | Navigasi dua tingkat dengan folder dan pemisah. Item menu dapat menunjuk ke module atau URL. |
| **Workflow** | Kanvas visual dengan langkah: Data Source, REST, Email, Page (tampilkan HTML), Module (pindah ke halaman lain), Condition (percabangan), Javascript (skrip di sandbox server), dan Form (meminta jawaban pengguna di tengah proses). Mendukung opsi satu transaksi SQL untuk seluruh workflow. |
| **Tracking** | Ringkasan jumlah run sukses, gagal, dan menunggu, grafik per hari, serta detail setiap langkah dari setiap run. |
| **Playground SQL** | Editor SQL dengan tab, penjelajah objek database, tampilan struktur tabel, dan perancang tabel baru. Secara default hanya baca dan selalu di-rollback. |
| **Admin** | Pengelolaan whitelist stored procedure dan halaman Health untuk memeriksa koneksi database. |

## Tech Stack

### Backend

| Komponen | Versi |
|---|---|
| .NET SDK | 10.0.301 |
| Target framework | `net10.0` |
| ASP.NET Core MVC (Razor Views) | 10.0 |
| Microsoft.Data.SqlClient | 7.1.1 |
| dbup-sqlserver (migrasi database) | 7.2.0 |
| HtmlSanitizer (pembersihan HTML) | 9.2.1039 |
| Jint (mesin Javascript untuk langkah skrip workflow) | 4.17.0 |
| MailKit (pengiriman email) | 4.18.1 |
| System.Security.Cryptography.Pkcs (dipatok karena celah keamanan pada versi transitif) | 10.0.13 |

Akses data memakai ADO.NET langsung (tanpa Entity Framework).

### Database

| Komponen | Versi |
|---|---|
| Microsoft SQL Server | Minimal 2016 SP1. Dikembangkan dan diuji dengan SQL Server 2025 Express (17.0.1000.7). |

### Frontend

Semua library disimpan di `src/Web/wwwroot/lib`, sehingga aplikasi tidak membutuhkan CDN atau `npm install`.

| Library | Versi |
|---|---|
| Bootstrap | 5.3.3 |
| Bootstrap Icons | 1.11.3 |
| jQuery | 3.7.1 |
| jQuery Validation | 1.21.0 |
| jQuery Validation Unobtrusive | 4.0.0 |
| Chart.js | 4.4.7 |
| Ace Editor | 1.36.5 |
| Inter (font) | variable font, latin dan latin-ext |

### Pengujian

| Komponen | Versi |
|---|---|
| xUnit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.4 |
| Microsoft.NET.Test.Sdk | 17.14.1 |
| coverlet.collector | 6.0.4 |

## Persyaratan Sistem

Sebelum menjalankan Rakit, pastikan komponen berikut sudah terpasang:

1. **Windows 10/11** atau Windows Server. Konfigurasi bawaan memakai Windows Authentication ke SQL Server.
2. **.NET SDK 10.0** (versi 10.0.301 atau lebih baru). Unduh dari [dotnet.microsoft.com](https://dotnet.microsoft.com/download). Periksa dengan:
   ```
   dotnet --version
   ```
3. **SQL Server 2016 SP1 atau lebih baru.** Edisi Express sudah cukup. Konfigurasi bawaan mengarah ke instance `.\SQLEXPRESS`.
4. **Git**, untuk mengambil kode sumber.
5. **sqlcmd** (opsional), untuk memasang data contoh tambahan.
6. Akun Windows yang dipakai harus dapat **membuat database** di instance tersebut (misalnya anggota peran `sysadmin` atau `dbcreator`). Pada instalasi SQL Server Express di komputer sendiri, akun yang memasang SQL Server biasanya sudah memiliki hak ini.

## Cara Menjalankan

### 1. Ambil kode sumber

```
git clone https://github.com/edrzvena/NetMvcRakit.git
cd NetMvcRakit
```

### 2. Pulihkan paket dan build

```
dotnet restore Rakit.slnx
dotnet build Rakit.slnx
```

### 3. Siapkan database

Jalankan migrasi. Langkah ini membuat database `Rakit` bila belum ada, lalu membuat schema, tabel metadata, user database, dan data contoh.

```
dotnet run --project db/Rakit.Migrations
```

Bila SQL Server Anda bukan `.\SQLEXPRESS`, berikan connection string sebagai argumen:

```
dotnet run --project db/Rakit.Migrations "Server=NAMA_SERVER;Database=Rakit;Integrated Security=true;TrustServerCertificate=true"
```

Migrasi aman dijalankan berulang kali. Skrip yang sudah diterapkan tercatat di tabel `dbo.SchemaVersions` dan tidak akan dijalankan ulang.

### 4. (Opsional) Pasang data contoh tambahan

Berkas `db/Samples/sample-data.sql` berisi 21 tabel dan 2 view contoh (karyawan, produk, pesanan, proyek, perpustakaan, dan lainnya) di schema `app`. Berkas ini bukan migrasi dan aman dijalankan ulang.

```
sqlcmd -S .\SQLEXPRESS -d Rakit -C -I -E -i db\Samples\sample-data.sql
```

### 5. Jalankan aplikasi

```
dotnet run --project src/Web
```

Buka **http://localhost:5247** di browser.

Perintah di atas otomatis memakai lingkungan `Development` (diatur di `src/Web/Properties/launchSettings.json`). Connection string bawaan ada di `src/Web/appsettings.Development.json`. Bila nama server SQL Anda berbeda, ubah kedua nilai di berkas tersebut (`Metadata` dan `Execution`).

### 6. Aktifkan penulisan data (untuk mencoba fitur tambah, ubah, hapus)

Secara default Rakit berjalan dalam mode aman: data hanya bisa dibaca. Untuk mencoba fitur penulisan di komputer pengembangan, nyalakan dua pengaturan berikut sebelum menjalankan aplikasi.

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

| Pengaturan | Fungsinya |
|---|---|
| `Rakit__Db__AllowWrite` | Mengizinkan data source bertipe *Command* (tambah, ubah, hapus data dari halaman dan workflow). |
| `Rakit__Playground__AllowWrite` | Mengizinkan Playground menyimpan perubahan dan mengizinkan wizard "Buat dari tabel" membuat stored procedure. |

Jangan aktifkan kedua pengaturan ini di server yang dapat diakses orang lain.

## Konsep Dasar untuk Pemula

Sebelum mulai membangun, pahami beberapa istilah berikut. Setiap istilah adalah satu "lapisan" dalam Rakit, dan lapisan berikutnya selalu dibangun di atas lapisan sebelumnya.

| Istilah | Penjelasan sederhana | Contoh |
|---|---|---|
| **Schema `app`** | Tempat data bisnis Anda (tabel, view, stored procedure). | `app.Customers` |
| **Schema `rakit`** | Tempat Rakit menyimpan konfigurasinya sendiri. Jangan diubah manual. | `rakit.Modules` |
| **Data Source** | Resep untuk mengambil atau mengubah data: dari tabel mana, query apa, atau stored procedure apa, dan dari mana nilai parameternya. | "Daftar pelanggan", "Simpan pelanggan baru" |
| **Statement Query / Command** | *Query* berarti hanya membaca data. *Command* berarti mengubah data dan butuh `AllowWrite`. | Daftar = Query, Simpan = Command |
| **Module** | Bentuk tampilan dari sebuah data source. | Tabel pelanggan, form pendaftaran, grafik penjualan |
| **Draft dan Published** | Module dan workflow baru selalu berstatus *Draft* dan belum bisa dibuka pengguna. Setelah di-*Publish*, barulah ia aktif. | |
| **Menu** | Tautan di navigasi atas yang mengarah ke module atau URL. | Menu "Pelanggan" |
| **Workflow** | Rangkaian langkah yang dijalankan ketika pengguna menekan sebuah tombol. | Simpan pesanan, lalu kirim email konfirmasi |
| **Placeholder** | Penanda `{{...}}` untuk menyisipkan nilai ke dalam teks langkah workflow. | `{{form.Email}}`, `{{key}}`, `{{simpan.row.Id}}` |
| **Tracking** | Riwayat setiap kali workflow dijalankan. | "Run tanggal 22 Okt gagal di langkah kirim-email" |
| **Whitelist SP** | Daftar stored procedure yang boleh dipakai. Stored procedure di luar daftar ini selalu ditolak. | `app.SpCustomers_Insert` |

Ringkasnya, urutan kerja di Rakit selalu sama:

1. Siapkan **data** di schema `app`.
2. Buat **Data Source** untuk membaca atau mengubah data itu.
3. Buat **Module** untuk menampilkannya.
4. **Publish** module tersebut.
5. Pasang di **Menu** agar mudah dibuka.
6. Bila perlu proses tambahan, buat **Workflow** dan pasang sebagai tombol di module.

## Tutorial: Membuat Halaman Pertama

Tutorial ini membuat halaman daftar pelanggan lengkap dengan fitur tambah, ubah, dan hapus, memakai tabel contoh `app.Customers` yang dibuat oleh migrasi.

**Persiapan:** jalankan aplikasi dengan `AllowWrite` aktif seperti pada [langkah 6](#6-aktifkan-penulisan-data-untuk-mencoba-fitur-tambah-ubah-hapus).

### Cara cepat: wizard "Buat dari tabel"

1. Buka **http://localhost:5247**, lalu pilih **Builder > Module** di menu samping.
2. Klik tombol untuk membuat module baru, isi nama (misalnya `Pelanggan`), dan pilih tipe **Table**.
3. Klik **Buat dari tabel...**, lalu pilih tabel `app.Customers`.
4. Periksa kolom yang ditampilkan dan field form yang disarankan, kemudian terapkan. Rakit akan:
   - membuat data source `customers-list`, `customers-detail`, `customers-insert`, `customers-update`, dan `customers-delete`;
   - membuat stored procedure CRUD bila belum ada (prosedur yang sudah ada, seperti `app.SpCustomers_Insert` dari migrasi, dipakai apa adanya dan tidak ditimpa), lalu memasukkannya ke whitelist;
   - mengisi tab Display (kolom) dan tab Form (field).
5. Klik **Simpan**. Module tersimpan sebagai **Draft**.
6. Kembali ke daftar module, lalu klik **Publish** pada module `Pelanggan`.
7. Buka **Builder > Menu**, tambahkan menu baru berlabel `Pelanggan`, pilih target **Module**, lalu pilih module `Pelanggan`.
8. Menu `Pelanggan` sekarang muncul di navigasi atas. Klik untuk membuka halaman. Alamat halamannya berbentuk `/object/page/{id-module}`.

### Cara manual: memahami setiap lapisan

Cara ini lebih panjang, tetapi membantu memahami apa yang dikerjakan wizard di balik layar.

1. **Buat data source untuk daftar.** Buka **Builder > Data Source**, buat data source baru:
   - Mode: **Table**, pilih `app.Customers`;
   - Statement: **Query**;
   - Klik **Preview** untuk memastikan data muncul, lalu simpan.
2. **Buat module.** Buka **Builder > Module**, buat module tipe **Table**:
   - Tab **Source**: pilih data source dari langkah 1 sebagai *List*;
   - Tab **Display**: muat kolom, atur label, format, dan kolom mana yang bisa diurutkan; tentukan **kolom kunci** (misalnya `Id`) bila nanti ingin ada tombol ubah atau hapus;
   - Tab **Preview**: lihat hasilnya sebelum disimpan.
3. **Tambahkan kemampuan menulis (opsional).** Buat data source bertipe **Command** untuk Insert, Update, dan Delete (biasanya memakai stored procedure), pilih di tab Source, lalu tambahkan field di tab **Form**. Stored procedure baru harus didaftarkan dulu di **Admin > SP Whitelist**.
4. **Publish** module dan **pasang di Menu** seperti pada cara cepat.

### Tipe module lain

| Tipe | Kapan dipakai |
|---|---|
| **Form** | Halaman isian mandiri, misalnya formulir pendaftaran. Hanya menambah data. |
| **Chart** | Grafik dari data source yang mengembalikan kolom label dan nilai. |
| **Widget** | Satu angka penting, misalnya total penjualan hari ini. |
| **Page** | Halaman HTML bebas. Sisipkan module lain dengan penanda `{{module:ID-MODULE}}` untuk membuat dashboard. Hanya module Table, Form, Chart, dan Widget yang dapat disematkan, maksimal 10 per halaman. |

## Tutorial: Membuat Workflow Pertama

Contoh berikut menambahkan tombol **Kirim Sapaan** di setiap baris tabel pelanggan yang menampilkan pesan berisi data pelanggan tersebut.

1. Buka **Builder > Workflow**, buat workflow baru dengan kode `sapa-pelanggan`.
2. Di kanvas, tambahkan langkah **Data Source** dengan kode `ambil`. Pilih data source detail pelanggan (`customers-detail`). Parameter `id` otomatis terisi dari kunci baris yang diklik.
3. Tambahkan langkah **Page** dengan isi HTML berikut, lalu sambungkan dari langkah `ambil`:
   ```html
   <h5>Halo, {{ambil.row.Name}}</h5>
   <p>Email terdaftar: {{ambil.row.Email}}</p>
   ```
4. Klik **Periksa** untuk memastikan tidak ada masalah, simpan, lalu **Publish**.
5. Buka module `Pelanggan`, tambahkan tombol di kotak **Aksi**:
   - Kode: `sapa`, Label: `Kirim Sapaan`;
   - Workflow: `sapa-pelanggan`;
   - Posisi: **per baris**.
6. Simpan dan buka halaman pelanggan. Setiap baris kini memiliki tombol **Kirim Sapaan**.
7. Buka **Builder > Tracking** untuk melihat riwayat run beserta status setiap langkahnya.

Nama kolom pada `{{ambil.row.Name}}` dan `{{ambil.row.Email}}` mengikuti kolom yang dikembalikan data source. Sesuaikan bila tabel Anda berbeda.

### Jenis langkah workflow

| Langkah | Fungsi | Output yang bisa dipakai langkah berikutnya |
|---|---|---|
| **Data Source** | Menjalankan data source (query atau stored procedure). | `{{kode.row.Kolom}}`, `{{kode.out.Parameter}}`, `{{kode.return}}` |
| **REST** | Memanggil API eksternal. Host tujuan harus terdaftar di `Rakit:Workflow:AllowedHosts`. | `{{kode.status}}`, `{{kode.body}}`, `{{kode.json.path.ke.nilai}}` |
| **Email** | Mengirim email lewat SMTP. Bila SMTP belum diatur, email hanya dicatat di log. | `{{kode.sent}}` |
| **Page** | Menampilkan HTML ke pengguna di jendela modal. | |
| **Module** | Mengarahkan pengguna ke halaman module lain, dapat membawa parameter. | |
| **Condition** | Memilih cabang *ya* atau *tidak* berdasarkan perbandingan nilai. | |
| **Javascript** | Menjalankan skrip di sandbox server (dibatasi waktu, memori, dan jumlah perintah). | `{{kode.body}}`, `{{kode.json.path}}` |
| **Form** | Menghentikan sementara workflow dan meminta pengguna mengisi form. Jawaban berlaku 30 menit. | `{{kode.button}}`, `{{kode.form.NamaField}}` |

Placeholder umum yang selalu tersedia: `{{form.NamaField}}` (nilai form), `{{key}}` (kunci baris), `{{claim.nama}}`, dan `{{now}}` (waktu saat ini).

## Konfigurasi

Semua pengaturan berada di `src/Web/appsettings.json` dan `src/Web/appsettings.Development.json`. Setiap pengaturan juga bisa diganti lewat environment variable dengan mengganti `:` menjadi `__` (dua garis bawah), misalnya `Rakit:Db:AllowWrite` menjadi `Rakit__Db__AllowWrite`.

### Connection string

| Kunci | Fungsi |
|---|---|
| `ConnectionStrings:Metadata` | Koneksi untuk membaca dan menyimpan konfigurasi Rakit (schema `rakit`). |
| `ConnectionStrings:Execution` | Koneksi untuk menjalankan data source (schema `app`). Wajib memakai `Pooling=false`. |

### Rakit:Db

| Kunci | Default | Fungsi |
|---|---|---|
| `AllowedSchemas` | `["app"]` | Schema yang boleh diakses data source. |
| `AllowRawQuery` | `false` | Mengizinkan data source mode *Query* (SQL mentah). |
| `AllowWrite` | `false` | Mengizinkan statement *Command* (menulis data). |
| `AllowStoredProcedure` | `true` | Mengizinkan data source mode *Stored Procedure*. |

### Rakit:Playground

| Kunci | Default | Fungsi |
|---|---|---|
| `Enabled` | `false` (`true` di Development) | Menampilkan Playground SQL. |
| `AllowWrite` | `false` | Mengizinkan Playground menyimpan perubahan dan wizard membuat stored procedure. |
| `MaxRows` | `1000` | Jumlah baris maksimal per hasil query. |
| `TimeoutSeconds` | `30` | Batas waktu eksekusi skrip. |
| `MaxScriptLength` | `200000` | Panjang skrip maksimal (karakter). |

### Rakit:Workflow

| Kunci | Default | Fungsi |
|---|---|---|
| `AllowedHosts` | `[]` | Host yang boleh dipanggil langkah REST, misalnya `api.contoh.com` (semua port) atau `localhost:5099` (port itu saja). Kosong berarti langkah REST tidak bisa berjalan. |
| `Script:TimeoutMs` | `500` | Batas waktu langkah Javascript. |
| `Script:MemoryLimitMb` | `16` | Batas memori langkah Javascript. |
| `Script:MaxStatements` | `100000` | Batas jumlah perintah yang dieksekusi. |
| `Script:MaxRecursion` | `100` | Batas kedalaman rekursi. |
| `Script:MaxResultBytes` | `65536` | Ukuran hasil skrip maksimal. |

### Rakit:Mail

| Kunci | Default | Fungsi |
|---|---|---|
| `Host`, `Port` | kosong, `25` | Server SMTP. Bila `Host` kosong, email hanya dicatat di log. |
| `Security` | `StartTlsWhenAvailable` | Mode keamanan koneksi SMTP. |
| `User`, `Password` | kosong | Kredensial SMTP. Simpan di user secrets atau environment variable, bukan di berkas yang di-commit. |
| `From`, `FromName` | kosong, `Rakit` | Alamat dan nama pengirim. |

### Rakit:Security

| Kunci | Default | Fungsi |
|---|---|---|
| `BuilderLocalOnly` | `true` | Builder dan Admin hanya bisa dibuka dari komputer tempat aplikasi berjalan. Akses dari komputer lain mendapat HTTP 403. |

## Struktur Proyek

```
NetMvcRakit/
├── Rakit.slnx                      Solution
├── db/
│   ├── Rakit.Migrations/           Program migrasi (DbUp)
│   │   └── Scripts/                Skrip SQL berurutan: 0001_schemas.sql, 0002_..., dst.
│   └── Samples/
│       └── sample-data.sql         Data contoh tambahan (bukan migrasi)
├── src/
│   ├── Core/                       Logika inti, tanpa ketergantungan ke UI
│   │   ├── Config/                 Kelas pengaturan (Rakit:Db, Rakit:Playground)
│   │   ├── Data/                   Pembuat koneksi database
│   │   ├── DataSources/            Data source: model, repository, eksekusi, resolusi parameter
│   │   ├── Health/                 Pemeriksaan kesehatan database
│   │   ├── Modules/                Module, menu, form, export/import CSV, wizard tabel
│   │   ├── Playground/             Playground SQL dan katalog objek database
│   │   ├── Procedures/             Whitelist stored procedure
│   │   ├── Schema/                 Penjelajah schema database
│   │   ├── Sql/                    Eksekusi SQL berparameter, template, transaksi
│   │   └── Workflows/              Workflow: model, validator, runner, langkah, tracking
│   └── Web/                        Aplikasi ASP.NET Core MVC
│       ├── Areas/
│       │   ├── Admin/              Whitelist SP dan Health
│       │   ├── Builder/            Module, Workflow, Tracking, Data Source, Menu, Playground
│       │   └── Object/             Halaman runtime yang dibuka pengguna (/object/page/{id})
│       ├── Security/               Pembatasan akses Builder dan header keamanan
│       ├── Views/                  Layout dan halaman beranda
│       └── wwwroot/                CSS, Javascript, dan library frontend
└── tests/
    └── Rakit.Core.Tests/           Pengujian xUnit
```

## Arsitektur dan Keamanan

### Satu database, dua schema

Rakit memakai satu database SQL Server yang dipisah menjadi dua schema:

- **`rakit`** menyimpan konfigurasi: data source, module, menu, workflow, tracking, dan whitelist.
- **`app`** menyimpan data bisnis beserta stored procedure-nya.

### User database dengan hak minimum

Aplikasi terhubung memakai identitas Windows, lalu pada setiap koneksi berpindah identitas ke user database tanpa login (`EXECUTE AS USER ... WITH NO REVERT`):

| User | Hak akses |
|---|---|
| `rakit_app` | Mengelola schema `rakit` dan membaca definisi objek di `app`. |
| `rakit_exec` | Membaca tabel dan menjalankan prosedur di schema `app`. Tidak memiliki akses ke schema `rakit`. |
| `rakit_play_ro` | Playground mode baca. |
| `rakit_play` | Playground mode tulis (hanya bila `Rakit:Playground:AllowWrite` aktif). |

Dengan pemisahan ini, query yang dijalankan sebuah data source tidak dapat membaca atau mengubah konfigurasi Rakit.

### Lapisan perlindungan lainnya

- Semua nilai dari pengguna dikirim sebagai parameter SQL, tidak pernah digabungkan ke teks SQL.
- Stored procedure wajib ada di whitelist dan diperiksa pada setiap eksekusi.
- Data source bertipe *Query* yang memakai SQL mentah selalu di-rollback setelah dibaca.
- Penulisan data, query mentah, dan Playground mode tulis mati secara default.
- Setiap aksi tulis memakai token anti-forgery.
- HTML pada module Page dibersihkan oleh HtmlSanitizer saat disimpan dan saat ditampilkan; HTML dari langkah Page dan Email workflow dibersihkan setiap kali workflow berjalan.
- Langkah REST hanya boleh memanggil host yang terdaftar dan tidak mengikuti redirect.
- Langkah Javascript berjalan di sandbox dengan batas waktu, memori, dan jumlah perintah.
- Tombol Tambah, Ubah, dan Hapus yang dimatikan di Builder juga ditolak di sisi server, bukan hanya disembunyikan dari tampilan.
- Header keamanan dipasang pada setiap respons (`X-Content-Type-Options`, `X-Frame-Options`, `Content-Security-Policy`, `Referrer-Policy`).
- Builder dan Admin hanya dapat diakses dari komputer lokal.

## Pengujian

Pengujian otomatis membutuhkan SQL Server dan akan membuat database terpisah bernama `Rakit_Test`, sehingga database `Rakit` tidak tersentuh.

```
dotnet test Rakit.slnx
```

Bila SQL Server Anda bukan `.\SQLEXPRESS`, atur environment variable `RAKIT_TEST_CONN` (tanpa nama database), misalnya:

```powershell
$env:RAKIT_TEST_CONN = "Server=NAMA_SERVER;Integrated Security=true;TrustServerCertificate=true"
dotnet test Rakit.slnx
```

## Status Proyek dan Batasan

Rakit masih dalam tahap pengembangan dan **belum siap dipasang di server publik**.

Sudah tersedia:

- Data source mode Table, Query, dan Stored Procedure.
- Module Table, Form, Chart, Widget, dan Page, beserta wizard "Buat dari tabel".
- Menu dinamis dua tingkat.
- Workflow dengan delapan jenis langkah, kanvas visual, transaksi tunggal, dan tracking.
- Playground SQL dan whitelist stored procedure.

Belum tersedia:

- **Login dan hak akses per pengguna.** Halaman runtime saat ini dapat dibuka siapa saja yang dapat menjangkau server. Pengaturan akses di konfigurasi module sudah disimpan tetapi belum ditegakkan.
- Penjadwalan workflow (misalnya menjalankan workflow setiap pagi).
- Log audit perubahan konfigurasi dan penampil log.
- Dukungan database selain SQL Server, dan lebih dari satu database.

## Pemecahan Masalah

| Gejala | Penyebab dan solusi |
|---|---|
| Migrasi gagal dengan pesan tidak bisa terhubung ke server | Pastikan layanan SQL Server berjalan dan nama instance benar. Berikan connection string sebagai argumen pada perintah migrasi. |
| Migrasi gagal karena tidak bisa membuat database | Akun Windows Anda belum memiliki hak membuat database. Minta peran `dbcreator` atau jalankan dengan akun administrator SQL Server. |
| Aplikasi gagal start dengan pesan `MetadataConnectionString is required` | Connection string kosong. Pastikan aplikasi berjalan di lingkungan `Development` atau isi `ConnectionStrings` di `appsettings.json`. |
| Builder menampilkan HTTP 403 | Builder dibuka dari komputer lain. Buka dari komputer tempat aplikasi berjalan, atau ubah `Rakit:Security:BuilderLocalOnly` khusus untuk lingkungan pengembangan yang tertutup. |
| Tombol Tambah, Ubah, atau Hapus tidak muncul | `Rakit:Db:AllowWrite` belum aktif, data source Insert/Update/Delete belum dipilih, kolom kunci belum diisi, atau tombolnya dimatikan di tab Display. |
| Halaman module menampilkan 404 | Module masih berstatus Draft. Publish dulu dari daftar module. |
| Data source ditolak dengan pesan whitelist | Daftarkan stored procedure tersebut di **Admin > SP Whitelist** dan pastikan statusnya aktif. |
| Langkah REST selalu gagal dengan pesan "tidak tersedia" | Host tujuan belum terdaftar di `Rakit:Workflow:AllowedHosts`. |
| Email tidak terkirim | `Rakit:Mail:Host` belum diisi, sehingga email hanya dicatat di log. Lengkapi pengaturan SMTP. |
| Wizard "Buat dari tabel" tidak bisa membuat stored procedure | Aktifkan `Rakit:Playground:Enabled` dan `Rakit:Playground:AllowWrite`. |
