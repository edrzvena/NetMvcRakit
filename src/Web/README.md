# Rakit.Web

## Menjalankan (dev)

```
dotnet run --project db/Rakit.Migrations      # buat/upgrade database Rakit di .\SQLEXPRESS01
dotnet run --project src/Web                  # jalankan web (Development)
```

Halaman: `/Admin/Health`, `/Builder/Schema`.

## Catatan penting

- **Tanpa autentikasi di F0.** Semua halaman terbuka. Hanya untuk dev lokal; jangan di-deploy sebelum fase auth (OIDC).
- Connection string ada di `appsettings.Development.json` (Windows auth, tanpa password). Override lewat
  env var `ConnectionStrings__Metadata` / `ConnectionStrings__Execution` atau `dotnet user-secrets`.
- Aplikasi konek dengan identitas Windows lalu meng-impersonate user database `rakit_app` (Metadata) dan
  `rakit_exec` (Execution). Jika aplikasi dijalankan sebagai identitas Windows lain (bukan pembuat database),
  identitas itu perlu `GRANT IMPERSONATE ON USER::rakit_app` dan `ON USER::rakit_exec`.
- Pooling dimatikan oleh `DbConnectionFactory` untuk kedua koneksi (konteks `NO REVERT` tidak aman di pool).
