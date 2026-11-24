using System.Security.Cryptography;
using System.Text.Json;
using Lazuli.Contracts;

namespace Lazuli.SolaraBackend;

/// <summary>
/// Atualiza a SolaraV3.dll pelo mesmo manifesto do atualizador oficial (06H §4):
/// <c>&lt;espelho&gt;/download/v3/manifest.json</c> com <c>version</c>, <c>changelog</c>, <c>supportedClient</c> e
/// <c>files[name, hash (SHA-256), size]</c>; o arquivo fica em <c>&lt;espelho&gt;/download/v3/&lt;name&gt;</c>.
/// A DLL baixada só é usada depois de conferir tamanho e SHA-256 contra o manifesto, e fica na pasta de dados do
/// backend (a pasta do plugin continua com a DLL empacotada, conferida pelo backend.json). Sem rede, usa a última
/// DLL verificada ou a empacotada.
/// </summary>
internal sealed class SolaraUpdater(string bundledDll, string storeDir)
{
    private static readonly string[] Mirrors = ["https://getsolara.dev", "https://getsolara.gg", "https://solara.gg"];
    private const string DllName = "SolaraV3.dll";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>Versão do Roblox que a DLL do manifesto suporta (ex.: "version-2c6fd5e09aaf4cfa"); null se não leu.</summary>
    public string? SupportedClient { get; private set; }

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return h;
    }

    private string CurrentFile => Path.Combine(storeDir, "current.txt");

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Diagnóstico: só grava com LAZULI_SOLARA_TRACE=1.</summary>
    private static void Dbg(string text)
    {
        if (Environment.GetEnvironmentVariable("LAZULI_SOLARA_TRACE") != "1") return;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "lazuli-solara-trace.txt"), $"{DateTime.Now:HH:mm:ss.fff} {text}\n"); } catch { }
    }

    /// <summary>Baixa a DLL em segundo plano logo na abertura do app (se ainda não houver nenhuma), para o primeiro Attach não esperar. Falhas são silenciosas: o Attach tenta de novo.</summary>
    public async Task PrefetchAsync()
    {
        try
        {
            Dbg("prefetch: start");
            Directory.CreateDirectory(storeDir);
            if (File.Exists(bundledDll) || LastVerified() is not null) { Dbg("prefetch: nada a baixar"); return; }
            var p = await ResolveDllAsync((l, m) => Dbg($"prefetch log {l}: {m}"), CancellationToken.None);
            Dbg($"prefetch: fim -> {p ?? "null"}");
        }
        catch (Exception ex) { Dbg("prefetch: erro " + ex); }
    }

    /// <summary>Devolve o caminho da DLL a carregar, baixando a versão nova se o manifesto tiver uma. Uma chamada por vez.</summary>
    public async Task<string?> ResolveDllAsync(Action<LogLevel, string> log, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ResolveCoreAsync(log, ct); }
        finally { _gate.Release(); }
    }

    private async Task<string?> ResolveCoreAsync(Action<LogLevel, string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(storeDir);
        // o plugin pode sair sem a DLL empacotada (ela é de terceiros): nesse caso vem sempre do manifesto oficial
        var hasBundled = File.Exists(bundledDll);
        var fallback = LastVerified() ?? (hasBundled ? bundledDll : null);

        var (mirror, manifest) = await FetchManifestAsync(ct);
        if (manifest is null)
        {
            log(LogLevel.Warning, "Couldn't read the Solara update manifest; using the current DLL.");
            return fallback;
        }
        SupportedClient = Str(manifest.Value, "supportedClient");

        var entry = FindFile(manifest.Value, DllName);
        if (entry is null) { log(LogLevel.Warning, "Solara manifest doesn't list SolaraV3.dll; using the current DLL."); return fallback; }
        var (hash, size) = entry.Value;

        if (hasBundled && await Sha256Async(bundledDll, ct) == hash) { Remember(null); return bundledDll; }

        var cached = Path.Combine(storeDir, $"SolaraV3-{hash[..16]}.dll");
        if (File.Exists(cached) && await Sha256Async(cached, ct) == hash) { Remember(cached); return cached; }

        log(LogLevel.Info, $"New SolaraV3.dll available ({Str(manifest.Value, "version")}: {Str(manifest.Value, "changelog")}). Downloading…");
        var tmp = cached + ".part";
        try
        {
            using (var resp = await Http.GetAsync($"{mirror}/download/v3/{DllName}", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }
            var len = new FileInfo(tmp).Length;
            var got = await Sha256Async(tmp, ct);
            if (len != size || got != hash)
                throw new InvalidDataException($"downloaded file doesn't match the manifest (size {len}/{size}, hash {got[..12]}…/{hash[..12]}…)");
            File.Move(tmp, cached, true);
            Remember(cached);
            Prune(cached);
            log(LogLevel.Success, "SolaraV3.dll updated and verified (SHA-256).");
            return cached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { File.Delete(tmp); } catch { }
            log(LogLevel.Warning, $"Couldn't update SolaraV3.dll ({ex.Message}); using the current DLL.");
            return fallback;
        }
    }

    /// <summary>Só consulta: true se o manifesto tem uma DLL diferente da que está em uso.</summary>
    public async Task<bool> HasUpdateAsync(string inUse, CancellationToken ct)
    {
        var (_, manifest) = await FetchManifestAsync(ct);
        if (manifest is null) return false;
        SupportedClient = Str(manifest.Value, "supportedClient") ?? SupportedClient;
        var entry = FindFile(manifest.Value, DllName);
        return entry is not null && await Sha256Async(inUse, ct) != entry.Value.Hash;
    }

    /// <summary>Lê o supportedClient do manifesto (se ainda não leu). null = sem rede ou o manifesto não informa.</summary>
    public async Task<string?> RefreshSupportedAsync(CancellationToken ct)
    {
        var (_, manifest) = await FetchManifestAsync(ct);
        if (manifest is not null) SupportedClient = Str(manifest.Value, "supportedClient") ?? SupportedClient;
        return SupportedClient;
    }

    private static async Task<(string Mirror, JsonElement? Manifest)> FetchManifestAsync(CancellationToken ct)
    {
        foreach (var m in Mirrors)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var text = await Http.GetStringAsync($"{m}/download/v3/manifest.json", cts.Token);
                using var doc = JsonDocument.Parse(text);
                return (m, doc.RootElement.Clone());
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        return ("", null);
    }

    private static (string Hash, long Size)? FindFile(JsonElement manifest, string name)
    {
        if (!manifest.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
        foreach (var f in files.EnumerateArray())
            if (Str(f, "name") == name && Str(f, "hash") is { Length: 64 } h && f.TryGetProperty("size", out var s) && s.TryGetInt64(out var size))
                return (h.ToLowerInvariant(), size);
        return null;
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private string? LastVerified()
    {
        try
        {
            var p = File.ReadAllText(CurrentFile).Trim();
            return p.Length > 0 && File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    private void Remember(string? path)
    {
        try { if (path is null) File.Delete(CurrentFile); else File.WriteAllText(CurrentFile, path); } catch { }
        if (path is null) Prune(null);
    }

    /// <summary>Apaga DLLs baixadas antigas (deixa só a atual).</summary>
    private void Prune(string? keep)
    {
        foreach (var f in Directory.EnumerateFiles(storeDir, "SolaraV3-*.dll"))
            if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch { }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
    }
}
