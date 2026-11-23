# Backends (plugins)

The app knows no backend at compile time. At startup it looks for folders `backends\<id>\` next to `Lazuli.exe`, reads
each `backend.json` manifest, verifies the files against the hashes in it, and only then loads the plugin DLL.
A backend implements `IBackendFactory` / `IRunSession` from `Lazuli.Contracts` (list clients, attach, run a script, kill, log lines).

## How a backend is chosen

1. **Settings → Interface** stores `BackendId` in `%APPDATA%\Lazuli\settings.json`.
2. At startup: the chosen id if it exists, otherwise the only installed backend, otherwise the built-in demo.
3. Any failure (missing file, wrong hash, bad manifest) falls back to the demo and prints a warning in the Output panel.

## Folder layout

```
backends\solara\
  Lazuli.Backend.Solara.dll        the plugin (the manifest's "assembly")
  Lazuli.Backend.Solara.deps.json
  backend.json                     manifest generated at build time (SHA-256 of every file)
```

```json
{ "id": "solara", "displayName": "Solara", "assembly": "Lazuli.Backend.Solara.dll",
  "factory": "Lazuli.SolaraBackend.SolaraFactory", "contractVersion": 1, "requiresAdmin": false,
  "files": { "Lazuli.Backend.Solara.dll": "<sha256>", "Lazuli.Backend.Solara.deps.json": "<sha256>" } }
```

## Writing a backend

1. A `net10.0` project in `backends\Lazuli.Backend.<Name>\` with a `ProjectReference` to `Lazuli.Contracts` and `Private="false"`
   (so the UI and the plugin share one copy of the contract types).
2. Set `BackendId`, `BackendDisplayName`, `BackendFactory` and `BackendRequiresAdmin` in the csproj and add
   `<Import Project="..\Backend.targets" />` (see `Lazuli.Backend.Solara.csproj`). That generates `backend.json` after every build.
3. To bundle it into the app, copy the project's output to `backends\<id>\` next to the exe
   (`dotnet build src\Lazuli.UI -p:Backend=solara` does this for Solara).

## Solara backend (`Lazuli.Backend.Solara`)

Does what the official Solara launcher does with `SolaraV3.dll`: `SetEventSink`, `Start(guid)` and the local HTTP bridge
(`scan_clients`, `Attach`, `execute_script`).

- **The DLL is third-party and not in this repository.** On first use the app downloads `SolaraV3.dll` from the official Solara site
  (`https://getsolara.dev/download/v3/manifest.json`, with the `.gg` mirrors), checks size and SHA-256 against that manifest and
  keeps it in `%LOCALAPPDATA%\Lazuli\backends\solara\dll\`. It is downloaded in the background right after startup and re-checked on every start.
  For offline development you can bundle a local copy with `-p:SolaraDir=<folder containing SolaraV3.dll>`.
- **CET is turned off (`CetCompat=false`) for this build only:** the DLL's protected code is incompatible with the shadow stack the
  .NET apphost enables by default (the process dies with `0xC0000409` right after Attach). The official launcher doesn't use CET either.
- **Files written by the DLL:** `CHRS`, `PLTBL`, `DLCTBL_FRTS` and `DEBUG.txt` in `bin\` next to the exe, like the official launcher.
- Debug trace: `LAZULI_SOLARA_TRACE=1` writes `bin\host-trace.txt` (bridge requests and DLL events).
- No administrator rights needed. The DLL still sends its own telemetry and checks Solara's ban lists, as in the official app.

## Security: what the hash check does and doesn't do

The `backend.json` hashes detect **corruption or accidental changes**. They do **not** prove who made a plugin: anyone who can write to
`backends\` can ship a plugin with a matching manifest, and a loaded plugin runs with the app's permissions. Only install backends from sources you trust,
and keep the app in a folder that only you can write to (the default per-user install folder is).
