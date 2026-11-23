# Automatic updates

At startup (during the splash) the app asks `<server>/main/latest.json`. If there is a newer version it shows an **Update** window
(release notes, size, "Update now" / "Later"). On accept it downloads with progress, verifies, swaps the files and relaunches itself.
It covers the whole app: `Lazuli.exe` and the backend plugin in `backends\solara\`.

**One channel: `main`.** Every build gets updates from it.

**Current server:** a Cloudflare Worker serving static files; the address is `UpdateConfig.DefaultBaseUrl`
(`src/Lazuli.UI/Updates/UpdateConfig.cs`).

## What goes in (and out of) the package
The release contains `Lazuli.exe` (with the .NET runtime and the plugin embedded) and the plugin folder `backends\solara\`
(our own code only). **It does not contain `SolaraV3.dll` or any other Solara file**: the app downloads it from the official site
(see `backends/README.md`). The release script fails if any such file ends up in the package.

## Security
- The manifest is **signed** (ECDSA P-256). The public key is in the app (`UpdateConfig.cs`); the private key lives only in
  `scripts\keys\update-private.pem` (git-ignored, **back it up**). Without a valid signature nothing is downloaded or installed.
- The manifest carries the **SHA-256** of the package; the app checks it while downloading. A tampered or corrupted package is discarded.
- HTTPS only (HTTP is allowed only on `localhost`, for tests). Paths inside the zip are validated.
- The file swap is **rolled back completely** if any file fails (for example locked by another program).
- If the server is down or the PC is offline the app opens normally (the check waits at most ~2.5 s on the splash).
- Packages larger than 25 MiB are split into 20 MiB parts (`.001`, `.002`, …) because Cloudflare static assets are limited to 25 MiB per file.
  Each part is retried up to 3 times and the SHA-256 of the whole is verified.

## Publishing an update
1. Build, package and sign (the app must be closed):
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-update.ps1 -Version 1.0.4 -Notes "Change A;Change B"
```
   Output: `publish-main\` (the app), `release\` (the single `Lazuli.exe` to hand out) and **`dist\main\`** (signed manifest + package parts).
2. Upload `dist\` with Wrangler (installed only in this project, under `tools\cloudflare`):
```powershell
cd tools\cloudflare; npx --no-install wrangler deploy --assets ..\..\dist --name <your-worker> --compatibility-date 2026-11-01
```
Options of `make-update.ps1`: `-Mandatory` (no "Later" button), `-MinVersion 1.0.0` (older versions are forced to update),
`-PackageBase https://...` (host the package elsewhere).

**Always raise the version number**: an equal or lower version is ignored, and an installed copy is only replaced by a newer one.

## First time / another machine (Cloudflare login)
```powershell
cd tools\cloudflare; npx --no-install wrangler login      # opens the browser
npx --no-install wrangler whoami
```
If `tools\cloudflare\node_modules` is missing run `npm install` there. To use another server change the deploy `--name` and `DefaultBaseUrl`.

## Testing without internet
```powershell
$env:LAZULI_UPDATE_URL = 'http://localhost:8099'   # serve dist\ on that port; the signature is still required
```
`LAZULI_NOUPDATE=1` turns the check off. In Debug builds `LAZULI_UPDATE_AUTO=1` accepts the update automatically.

## Notes
- The app must be able to write to its own folder (so don't install under `Program Files`; the per-user folder works).
- Old files become `*.old` during the swap and are deleted on the next start.
- An update never deletes files the new version no longer has (it only replaces/adds).
- User data lives in `%APPDATA%\Lazuli` (settings, scripts) and `%LOCALAPPDATA%\Lazuli` (cache, downloads, logs).
