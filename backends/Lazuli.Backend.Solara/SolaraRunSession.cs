using System.Diagnostics;
using System.Text.Json;
using Lazuli.Contracts;

namespace Lazuli.SolaraBackend;

/// <summary>
/// Liga a UI (Attach / Executar / clientes / console) à SolaraV3.dll original, fazendo o papel do SolaraV3.exe:
/// prepara a pasta de trabalho (bin\, asset inicial DLCTBL_FRTS, cópia da DLL), registra o SetEventSink,
/// gera o GUID (UUID v4), chama Start(guid) e conversa com a ponte HTTP local.
/// Não roda o atualizador do Solara: para outra versão da DLL, troque o arquivo e recompile o plugin.
/// </summary>
public sealed class SolaraRunSession : IRunSession
{
    private const string RobloxProcess = "RobloxPlayerBeta";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    private readonly string _backendDir;
    private readonly string _workDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SolaraBridge? _bridge;
    private bool _startTried;
    private readonly SolaraUpdater _updater;
    private string? _loadedDll;
    private Timer? _updateTimer;
    private bool _updateNotified;
    private volatile bool _banned;
    private string _status = "Solara · not started";

    private readonly object _cacheLock = new();
    private List<SolaraClient> _cache = [];
    private DateTime _cacheAt = DateTime.MinValue;

    public SolaraRunSession(string backendDirectory, string dataDirectory)
    {
        _backendDir = backendDirectory;
        // a DLL grava os assets (CHRS, PLTBL, DLCTBL_FRTS) em "<pasta do exe>\bin" e o DEBUG.txt em "<dir. atual>\bin":
        // como o launcher oficial, usa a pasta do exe para as duas coisas
        _workDir = Path.GetDirectoryName(Environment.ProcessPath) ?? dataDirectory;
        _updater = new SolaraUpdater(Path.Combine(backendDirectory, "SolaraV3.dll"), Path.Combine(dataDirectory, "dll"));
        // já na abertura, em segundo plano: baixa a DLL do site oficial (se faltar) para o primeiro Attach não esperar
        _ = Task.Run(async () => { await Task.Delay(1500); await _updater.PrefetchAsync(); });
    }

    public string StatusText => _status;
    public event Action<LogLine>? LogReceived;

    public bool IsAttached => _bridge is not null && Cached().Any(c => c.Attached);

    public bool IsTargetRunning
    {
        get
        {
            try
            {
                var ps = Process.GetProcessesByName(RobloxProcess);
                foreach (var p in ps) p.Dispose();
                return ps.Length > 0;
            }
            catch { return false; }
        }
    }

    // ---------- inicialização (o que o launcher oficial faz antes de abrir a UI) ----------

    private async Task<bool> EnsureStartedAsync(List<LogLine> lines, CancellationToken ct)
    {
        void Add(LogLevel l, string t) => lines.Add(new LogLine(DateTime.Now, l, t));
        if (_bridge is not null) return true;
        if (_banned) { Add(LogLevel.Error, new SolaraBannedException().Message); return false; }
        if (_startTried)
        {
            Add(LogLevel.Error, "Solara already failed to start in this session. Restart the app.");
            return false;
        }

        try
        {
            // pasta de trabalho: a DLL usa caminhos relativos ("bin\DEBUG.txt", "bin\<assets>") ao diretório atual
            // mesma fonte do atualizador oficial: baixa e confere a DLL nova, se houver (só vale antes do Start)
            _status = "Solara · checking for updates…";
            var dll = await _updater.ResolveDllAsync(Add, ct);
            if (dll is null)
            {
                _status = "Solara · download failed";
                Add(LogLevel.Error, "Couldn't download SolaraV3.dll. Check your internet connection and click Attach again.");
                return false;
            }
            _loadedDll = dll;

            var bin = Path.Combine(_workDir, "bin");
            var dllDir = Path.Combine(bin, "dll");
            Directory.CreateDirectory(dllDir);

            // asset inicial: o launcher oficial grava o DLCTBL_FRTS embutido antes de carregar a DLL (06H §4); a DLL o atualiza depois
            var seed = Path.Combine(_backendDir, "DLCTBL_FRTS.seed");
            var asset = Path.Combine(bin, "DLCTBL_FRTS");
            if (!File.Exists(asset) && File.Exists(seed)) File.Copy(seed, asset);

            // como o oficial: carrega uma cópia (bin\dll\SolaraV3_loaded_<ts>.dll), e não o arquivo original
            foreach (var old in Directory.EnumerateFiles(dllDir, "SolaraV3_loaded_*.dll"))
                try { File.Delete(old); } catch { /* em uso por outra instância */ }
            var copy = Path.Combine(dllDir, $"SolaraV3_loaded_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.dll");
            File.Copy(dll, copy, true);

            Environment.CurrentDirectory = _workDir;
            // rastreamento de diagnóstico (requisições à ponte e eventos da DLL): só com LAZULI_SOLARA_TRACE=1
            if (Environment.GetEnvironmentVariable("LAZULI_SOLARA_TRACE") == "1")
                SolaraNative.TracePath = Path.Combine(_workDir, "bin", "host-trace.txt");
            SolaraNative.Trace($"--- host start, exe={Environment.ProcessPath}, dll={copy}");

            _status = "Solara · iniciando…";
            SolaraNative.Event -= OnNativeEvent;
            SolaraNative.Event += OnNativeEvent;
            SolaraNative.Load(copy);

            _startTried = true;
            var guid = Guid.NewGuid().ToString("D");
            var code = await Task.Run(() => SolaraNative.Start(guid));
            SolaraNative.Trace($"Start() = {code}");

            if (code == 2)
            {
                _banned = true;
                _status = "Solara · banned";
                Add(LogLevel.Error, new SolaraBannedException().Message);
                return false;
            }
            if (code == -1)
            {
                _status = "Solara · failed to start";
                Add(LogLevel.Error, $"SolaraV3.dll Start() returned -1 (failure). See {Path.Combine(bin, "DEBUG.txt")}.");
                return false;
            }
            if (code < 1 || code > 65535)
            {
                _status = "Solara · failed to start";
                Add(LogLevel.Error, $"Start() returned an invalid port ({code}).");
                return false;
            }

            _bridge = new SolaraBridge(code, guid);
            _status = "Solara · ready";
            var ver = FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "?";
            Add(LogLevel.Info, $"Solara {ver} started (local bridge on port {code}). DLL log: {Path.Combine(bin, "DEBUG.txt")}");
            // a DLL só aceita um Start por processo: uma versão nova lançada depois só entra ao reabrir o app
            _updateTimer = new Timer(_ => _ = CheckForUpdateAsync(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _status = "Solara · failed to load";
            Add(LogLevel.Error, $"Windows couldn't load SolaraV3.dll ({ex.Message}). If your antivirus blocked it, allow the app folder.");
            return false;
        }
        catch (Exception ex)
        {
            _status = "Solara · error";
            Add(LogLevel.Error, ex.Message);
            return false;
        }
    }

    // ---------- clientes ----------

    private List<SolaraClient> Cached()
    {
        lock (_cacheLock) return _cache;
    }

    private async Task<List<SolaraClient>> ScanAsync(CancellationToken ct, bool force = false)
    {
        var bridge = _bridge;
        if (bridge is null) return [];
        lock (_cacheLock)
            if (!force && DateTime.UtcNow - _cacheAt < CacheTtl) return _cache;
        var list = await bridge.ScanAsync(ct);
        lock (_cacheLock) { _cache = list; _cacheAt = DateTime.UtcNow; }
        return list;
    }

    public IReadOnlyList<ClientInfo> GetClients()
    {
        var result = new List<ClientInfo>();
        List<SolaraClient> sc;
        try { sc = _bridge is null ? [] : ScanAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (SolaraBannedException) { _banned = true; _status = "Solara · banned"; sc = []; }
        catch { sc = Cached(); }

        Process[] ps;
        try { ps = Process.GetProcessesByName(RobloxProcess); } catch { ps = []; }
        var seen = new HashSet<int>();
        foreach (var p in ps.OrderBy(p => p.Id))
        {
            using (p)
            {
                seen.Add(p.Id);
                var s = sc.FirstOrDefault(c => c.Id == p.Id);
                var parts = new List<string> { StatusLabel(s) };
                try { parts.Add($"{p.WorkingSet64 / 1048576} MB"); } catch { }
                try { parts.Add(Uptime(DateTime.Now - p.StartTime)); } catch { }
                var title = s is { Username.Length: > 0 } ? s.Username : "Roblox";
                var tip = _bridge is null ? "Click Attach to start Solara."
                    : s is null ? "Not listed by Solara yet." : $"Solara state: {s.Status}";
                result.Add(new ClientInfo(p.Id, title, string.Join(" · ", parts), s?.Attached == true, tip));
            }
        }
        // client_id que não bate com nenhum PID (o relatório trata client_id = PID como hipótese): mostra assim mesmo
        foreach (var s in sc.Where(c => !seen.Contains(c.Id)))
            result.Add(new ClientInfo(s.Id, s.Username.Length > 0 ? s.Username : $"Client {s.Id}", StatusLabel(s), s.Attached,
                "Solara client_id with no matching process."));
        return result;
    }

    private static string StatusLabel(SolaraClient? s) => s?.Status switch
    {
        1 => "In game",
        2 => "Attached, waiting for game",
        _ => "Not attached",
    };

    private static string Uptime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : "just now";

    // ---------- attach ----------

    public async Task<IReadOnlyList<LogLine>> AttachAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        var lines = new List<LogLine>();
        void Add(LogLevel l, string t) => lines.Add(new LogLine(DateTime.Now, l, t));
        try
        {
            if (!IsTargetRunning)
            {
                _status = "Solara · Roblox closed";
                Add(LogLevel.Warning, "Roblox isn't open. Open the game and try again.");
                return lines;
            }
            if (!await EnsureStartedAsync(lines, ct)) return lines;
            // versão errada: a DLL não anexa e só desistiria depois de ~30 s esperando; recusa na hora (como o Solara oficial)
            if (!CheckRobloxVersion(lines))
            {
                _status = "Solara · unsupported Roblox version";
                return lines;
            }
            var bridge = _bridge!;

            // a DLL descobre os processos sozinha; dá alguns segundos para ela listar
            var clients = await ScanAsync(ct, force: true);
            for (var i = 0; i < 10 && clients.Count == 0; i++)
            {
                await Task.Delay(1000, ct);
                clients = await ScanAsync(ct, force: true);
            }
            if (clients.Count == 0)
            {
                _status = "Solara · no clients";
                Add(LogLevel.Error, "Solara found no open Roblox.");
                return lines;
            }

            var pending = clients.Where(c => !c.Attached).ToList();
            foreach (var c in pending)
            {
                _status = "Solara · attaching…";
                var raw = await bridge.AttachAsync(c.Id, ct);
                if (raw == "^")
                    Add(LogLevel.Error, $"Client {c.Id}: Solara couldn't read the Roblox version (Roblox may be running as administrator).");
                else if (raw == ">")
                    Add(LogLevel.Error, $"Client {c.Id}: this Roblox version isn't supported by this SolaraV3.dll yet.");
            }

            // o attach termina em segundo plano: espera os clientes chegarem a "no jogo"
            var want = clients.Select(c => c.Id).ToHashSet();
            for (var i = 0; i < 60; i++)
            {
                clients = await ScanAsync(ct, force: true);
                if (clients.Where(c => want.Contains(c.Id)).All(c => c.Ready)) break;
                await Task.Delay(500, ct);
            }

            var ready = clients.Count(c => c.Ready);
            var waiting = clients.Count(c => c.Status == 2);
            if (ready > 0)
            {
                _status = "Solara · attached";
                Add(LogLevel.Success, ready == 1 ? "Attached to Roblox." : $"Attached to {ready} clients.");
            }
            else if (waiting > 0)
            {
                _status = "Solara · waiting for game";
                Add(LogLevel.Warning, "Attached, but Roblox isn't in a game yet. Join a game to execute scripts.");
            }
            else
            {
                _status = "Solara · attach failed";
                Add(LogLevel.Error, $"Timed out: no client attached. See {Path.Combine(_workDir, "bin", "DEBUG.txt")}.");
            }
        }
        catch (SolaraBannedException ex)
        {
            _banned = true;
            _status = "Solara · banned";
            Add(LogLevel.Error, ex.Message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _status = "Solara · error";
            Add(LogLevel.Error, ex.Message);
        }
        finally { _gate.Release(); }
        return lines;
    }

    public void Detach() => _status = "Solara · detached";

    // ---------- executar ----------

    public Task<IReadOnlyList<LogLine>> RunAsync(string source, CancellationToken ct = default) => RunOnAsync(source, null, ct);

    public async Task<IReadOnlyList<LogLine>> RunOnAsync(string source, IReadOnlyCollection<int>? pids, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_bridge is null)
                return [new(DateTime.Now, LogLevel.Error, "Not attached. Click Attach first.")];

            var clients = await ScanAsync(ct, force: true);
            var chosen = pids is null ? clients : clients.Where(c => pids.Contains(c.Id)).ToList();
            var targets = chosen.Where(c => c.Ready).ToList();
            if (targets.Count == 0)
            {
                var msg = chosen.Any(c => c.Status == 2)
                    ? "Roblox is attached but not in a game yet."
                    : pids is { Count: > 0 } ? "None of the selected clients is attached." : "Not attached. Click Attach first.";
                return [new(DateTime.Now, LogLevel.Error, msg)];
            }

            var lines = new List<LogLine>();
            var ok = 0;
            foreach (var c in targets)
            {
                try { await _bridge.ExecuteAsync(c.Id, source, ct); ok++; }
                catch (SolaraBannedException) { throw; }
                catch (Exception ex) { lines.Add(new(DateTime.Now, LogLevel.Error, $"Client {c.Id}: {ex.Message}")); }
            }
            if (ok > 0)
                lines.Insert(0, new(DateTime.Now, LogLevel.Success, ok == 1 ? $"Script sent to client {targets[0].Id}." : $"Script sent to {ok} clients."));
            return lines;
        }
        catch (SolaraBannedException ex)
        {
            _banned = true;
            _status = "Solara · banned";
            return [new(DateTime.Now, LogLevel.Error, ex.Message)];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _status = "Solara · error";
            return [new(DateTime.Now, LogLevel.Error, ex.Message)];
        }
        finally { _gate.Release(); }
    }

    // ---------- atualização ----------

    public async Task<string?> GetSupportedTargetVersionAsync(CancellationToken ct = default) =>
        _updater.SupportedClient ?? await _updater.RefreshSupportedAsync(ct);

    /// <summary>Compara a pasta de versão do Roblox aberto (…\Versions\version-xxx\) com o supportedClient do manifesto.</summary>
    /// <returns>false quando nenhum Roblox aberto é da versão suportada (não adianta tentar anexar).</returns>
    private bool CheckRobloxVersion(List<LogLine> lines)
    {
        var supported = _updater.SupportedClient;
        if (string.IsNullOrEmpty(supported)) return true; // não sabe: tenta assim mesmo
        var wrong = new List<string>();
        var matching = 0;
        foreach (var p in Process.GetProcessesByName(RobloxProcess))
        {
            using (p)
            {
                string? folder = null;
                try { folder = Path.GetFileName(Path.GetDirectoryName(p.MainModule?.FileName)); } catch { }
                if (folder is not { Length: > 0 } || !folder.StartsWith("version-", StringComparison.OrdinalIgnoreCase)) { matching++; continue; } // não deu para ler: deixa a DLL decidir
                if (folder.Equals(supported, StringComparison.OrdinalIgnoreCase)) matching++;
                else if (!wrong.Contains(folder)) wrong.Add(folder);
            }
        }
        if (wrong.Count == 0) return true;
        if (matching > 0)
        {
            lines.Add(new(DateTime.Now, LogLevel.Warning,
                $"Some open Roblox windows ({string.Join(", ", wrong)}) aren't the version Solara supports ({supported}); only the others can attach."));
            return true;
        }
        lines.Add(new(DateTime.Now, LogLevel.Error,
            $"Attach failed: Roblox {string.Join(", ", wrong)} isn't supported by this Solara build (supported: {supported})."));
        return false;
    }

    private async Task CheckForUpdateAsync()
    {
        if (_updateNotified || _loadedDll is null) return;
        try
        {
            if (await _updater.HasUpdateAsync(_loadedDll, CancellationToken.None))
            {
                _updateNotified = true;
                LogReceived?.Invoke(new LogLine(DateTime.Now, LogLevel.Warning,
                    "A Solara update is out. Restart the app to use it (downloaded and verified automatically)."));
            }
        }
        catch { }
    }

    // ---------- eventos da DLL (SetEventSink) ----------

    private void OnNativeEvent(int kind, string json)
    {
        if (kind == 2)
        {
            lock (_cacheLock) _cacheAt = DateTime.MinValue; // cliente mudou: próxima leitura vai à ponte
            return;
        }
        if (kind != 1 || LogReceived is null) return;

        // kind 1: {"client_id":<int>,"lines":[{"m":texto,"t":Enum.MessageType,"ts":ms}],"dropped":<n>}
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("client_id", out var cid) && cid.TryGetInt32(out var n) ? n : 0;
        if (root.TryGetProperty("lines", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in arr.EnumerateArray())
            {
                var text = l.TryGetProperty("m", out var m) ? m.GetString() ?? "" : "";
                var t = l.TryGetProperty("t", out var tt) && tt.TryGetInt32(out var tv) ? tv : 0;
                var time = l.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.Now;
                var level = t switch { 2 => LogLevel.Warning, 3 => LogLevel.Error, _ => LogLevel.Info };
                LogReceived?.Invoke(new LogLine(time, level, $"[{id}] {text}"));
            }
        }
        if (root.TryGetProperty("dropped", out var d) && d.TryGetInt32(out var dropped) && dropped > 0)
            LogReceived?.Invoke(new LogLine(DateTime.Now, LogLevel.Warning, $"[{id}] {dropped} console lines were dropped."));
    }

    // ---------- encerrar ----------

    /// <summary>Encerra processos do Roblox. Só aceita processos chamados RobloxPlayerBeta (nunca outro programa).</summary>
    public Task<IReadOnlyList<LogLine>> KillAsync(IReadOnlyCollection<int>? pids, CancellationToken ct = default) => Task.Run<IReadOnlyList<LogLine>>(() =>
    {
        var lines = new List<LogLine>();
        void Add(LogLevel l, string t) => lines.Add(new LogLine(DateTime.Now, l, t));

        var targets = pids is null
            ? Process.GetProcessesByName(RobloxProcess).Select(p => { var id = p.Id; p.Dispose(); return id; }).ToList()
            : pids.Distinct().ToList();
        if (targets.Count == 0) { Add(LogLevel.Info, "Roblox is already closed."); return lines; }

        var killed = 0;
        foreach (var pid in targets)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.ProcessName.Equals(RobloxProcess, StringComparison.OrdinalIgnoreCase))
                {
                    Add(LogLevel.Warning, $"PID {pid} isn't Roblox; ignored.");
                    continue;
                }
                p.Kill(true);
                p.WaitForExit(3000);
                killed++;
                Add(LogLevel.Info, $"PID {pid} encerrado.");
            }
            catch (ArgumentException) { Add(LogLevel.Info, $"PID {pid} had already closed."); }
            catch (Exception ex) { Add(LogLevel.Error, $"Couldn't kill PID {pid}: {ex.Message}"); }
        }
        if (killed > 0) _status = "Solara · Roblox encerrado";
        if (killed > 1) Add(LogLevel.Success, $"Closed {killed} Roblox processes.");
        return lines;
    }, ct);
}
