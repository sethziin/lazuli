# Lazuli

A clean, fast script editor and executor UI for Windows, built with [Avalonia](https://avaloniaui.net/) and .NET 10.

- **Two layouts:** *Simple* (just the editor and a few buttons) and *Detailed* (Home, Script Hub, side panels).
- **Script Hub** powered by [ScriptBlox](https://scriptblox.com): search, categories, one-click execute or open in the editor.
- **Themes:** seven built-in color themes, custom colors, or your own background image (with readability controls).
- **Plugin backends:** the UI talks to a small interface (`IRunSession`); the code that actually attaches to the game lives in a separate plugin (`backends/`).
- **Self-contained:** the release `.exe` includes .NET, installs itself per-user (no admin) and updates itself with signed, hash-checked packages.

> **Disclaimer.** Using script executors violates Roblox's Terms of Use and can get your account banned.
> This project is not affiliated with Roblox, Solara or ScriptBlox. It ships **no third-party binaries**:
> the Solara backend downloads `SolaraV3.dll` from the official Solara site on first use and checks its SHA-256 against the official manifest.
> Use it at your own risk.

## Download

Grab `Lazuli.exe` from the [Releases](../../releases) page and run it. It installs to `%LOCALAPPDATA%\Programs\Lazuli`.
Windows 10 (1607) or newer, 64-bit. If the Microsoft Visual C++ Runtime is missing, Lazuli offers to download the official one.

Windows SmartScreen / Smart App Control may warn about or block the file, because it isn't signed with a paid certificate
(see [Signing](#signing)). Antivirus programs may also flag the downloaded Solara DLL; that is a third-party injector.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
# run the app with the Solara backend
dotnet build src/Lazuli.UI -c Release -p:Backend=solara
src/Lazuli.UI/bin/Release/net10.0/win-x64/Lazuli.exe
```

Release package (single self-contained exe + signed update package in `dist/`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/make-update.ps1 -Version 1.2.3 -Notes "Change A;Change B"
```

## Project layout

| Path | What it is |
|---|---|
| `src/Lazuli.UI` | The Avalonia app (editor, layouts, Script Hub, themes, updater, installer) |
| `src/Lazuli.Contracts` | The small interface between the UI and a backend plugin |
| `backends/Lazuli.Backend.Solara` | Backend plugin for Solara (loaded from `backends/solara` at runtime) |
| `backends/Lazuli.Backend.TestFixture` | A fake backend used by tests |
| `tools/UpdateTool` | Creates and signs update packages |
| `scripts/` | Release scripts |

See [backends/README.md](backends/README.md) for how to write a backend and [README-UPDATE.md](README-UPDATE.md) for the update system.

## Using your own update server and keys

The official build checks `UpdateConfig.DefaultBaseUrl` and trusts only manifests signed by the key in `UpdateConfig.PublicKeySpki`.
If you fork and publish your own builds, create your own keys and server:

```powershell
dotnet run --project tools/UpdateTool -- keygen scripts/keys
```

then put the new public key and your URL in `src/Lazuli.UI/Updates/UpdateConfig.cs`. **Never commit `scripts/keys/`** (it holds the private key; it is git-ignored).

## Signing

Trusted code-signing certificates are paid. `Lazuli.exe` is signed with a self-signed certificate (`CN=Lazuli`, created by `scripts/make-cert.ps1`),
timestamped by DigiCert's free timestamp service. That identifies the file as Lazuli's and detects tampering after signing, but it does **not**
make Windows trust it, so SmartScreen may still ask on first run.

The backend plugin DLL is intentionally **not** signed: with a self-signed signature, Windows Smart App Control blocks loading it (tested),
while the unsigned DLL loads. Its integrity is protected by the SHA-256 hashes in `backend.json`.

What the app does to keep Windows warnings to a minimum, without bypassing anything: it runs as a normal user (never asks for administrator),
carries proper version information, installs per-user as a fresh file (so the "downloaded from the internet" mark of the file you already
approved is not copied), and downloads updates itself over HTTPS with signature and hash checks.
Free trusted signing exists only for some open-source projects (for example through the SignPath Foundation); whether this project qualifies is up to them.

## Contributing

Issues and pull requests are welcome. Please keep third-party binaries out of the repository.

## License

[MIT](LICENSE)
