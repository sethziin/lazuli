using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Executor.UI.Updates;

namespace Executor.UI.Services;

/// <summary>Um script do ScriptBlox (campos que o Hub usa).</summary>
public sealed record SbScript(
    string Id, string Title, string Slug, string Game, string Code,
    bool Verified, bool Key, bool Universal, bool Patched, long Views, string? ImageUrl, DateTime? Updated);

public sealed record SbPage(IReadOnlyList<SbScript> Items, int Page, int TotalPages)
{
    public bool HasMore => Page < TotalPages;
}

/// <summary>Erro já em linguagem de usuário (rede fora, limite de requisições, resposta estranha…).</summary>
public sealed class SbException(string message) : Exception(message);

/// <summary>
/// Cliente da API pública do ScriptBlox (https://docs.scriptblox.com): /api/script/fetch, /search e /trending.
/// Os resultados de um endpoint não são guardados em disco (mudam toda hora); só as miniaturas ficam em cache.
/// A documentação pede a atribuição "Powered by ScriptBlox.com" em área visível: ela aparece no Hub.
/// </summary>
public static class ScriptBlox
{
    public const string Site = "https://scriptblox.com";
    private const string Api = Site + "/api/script/";

    private static readonly HttpClient Http = Create();
    private static readonly SemaphoreSlim ImageGate = new(4, 4);

    private static HttpClient Create()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"Lazuli/{AppInfo.VersionText}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return h;
    }

    /// <summary>Filtros do Hub. Sempre sem scripts "patched" e só gratuitos (o que quase todo mundo quer).</summary>
    public sealed record Query(string? Text, string Kind, int Page)
    {
        // Kind: latest | trending | popular | verified | universal | nokey
        public string BuildUrl()
        {
            var search = !string.IsNullOrWhiteSpace(Text);
            if (Kind == "trending" && !search) return Api + "trending?max=20";

            var p = new List<string> { "max=20", $"page={Page}", "mode=free", "patched=0" };
            if (search)
            {
                p.Insert(0, "q=" + Uri.EscapeDataString(Text!.Trim()));
                p.Add(Kind == "popular" ? "sortBy=views" : "sortBy=accuracy");
            }
            else if (Kind == "popular") p.Add("sortBy=views");
            switch (Kind)
            {
                case "verified": p.Add("verified=1"); break;
                case "universal": p.Add("universal=1"); break;
                case "nokey": p.Add("key=0"); break;
            }
            return Api + (search ? "search?" : "fetch?") + string.Join('&', p);
        }
    }

    public static async Task<SbPage> GetAsync(Query q, CancellationToken ct)
    {
        string text;
        try
        {
            using var resp = await Http.GetAsync(q.BuildUrl(), ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new SbException("Too many requests. Wait a few seconds and try again.");
            text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new SbException(ApiMessage(text) ?? $"ScriptBlox returned an error ({(int)resp.StatusCode}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SbException("ScriptBlox took too long to answer. Try again.");
        }
        catch (HttpRequestException)
        {
            throw new SbException("Can't reach ScriptBlox. Check your internet connection.");
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("result", out var result))
                throw new SbException(ApiMessage(text) ?? "Unexpected answer from ScriptBlox.");
            var items = new List<SbScript>();
            if (result.TryGetProperty("scripts", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (Parse(e) is { } s) items.Add(s);
            var total = result.TryGetProperty("totalPages", out var tp) && tp.TryGetInt32(out var t) ? t : 1;
            return new SbPage(items, q.Page, Math.Max(1, total));
        }
        catch (JsonException)
        {
            throw new SbException("Unexpected answer from ScriptBlox.");
        }
    }

    private static string? ApiMessage(string text)
    {
        try
        {
            using var d = JsonDocument.Parse(text);
            return d.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch { return null; }
    }

    private static SbScript? Parse(JsonElement e)
    {
        var code = Str(e, "script");
        var title = Str(e, "title");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(title)) return null;
        string game = "";
        if (e.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.Object) game = Str(g, "name") ?? "";
        DateTime? updated = null;
        if (DateTime.TryParse(Str(e, "lastUpdate") ?? Str(e, "updatedAt") ?? Str(e, "createdAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) updated = d;
        return new SbScript(
            Str(e, "_id") ?? title, title.Trim(), Str(e, "slug") ?? "", game.Trim(), code,
            Bool(e, "verified"), Bool(e, "key"), Bool(e, "isUniversal"), Bool(e, "isPatched"),
            e.TryGetProperty("views", out var v) && v.TryGetInt64(out var n) ? n : 0,
            Str(e, "image") ?? (e.TryGetProperty("game", out var g2) && g2.ValueKind == JsonValueKind.Object ? Str(g2, "imageUrl") : null),
            updated);
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    public static string ViewsText(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    // ---------- miniaturas ----------

    /// <summary>Só HTTPS e só dos hosts do próprio ScriptBlox/Roblox (a URL vem da resposta de um servidor externo).</summary>
    private static Uri? SafeImageUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(new Uri(Site), url, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        var h = u.Host.ToLowerInvariant();
        var ok = h == "scriptblox.com" || h.EndsWith(".scriptblox.com") || h.EndsWith(".rbxcdn.com") || h == "rbxcdn.com";
        return ok ? u : null;
    }

    /// <summary>Baixa (ou lê do cache) a miniatura e devolve o caminho do arquivo local; null se não der. Cache em %LOCALAPPDATA%.</summary>
    public static async Task<string?> GetImageFileAsync(string? url, CancellationToken ct)
    {
        var uri = SafeImageUri(url);
        if (uri is null) return null;
        var dir = Path.Combine(Storage.CacheDir, "hub");
        var file = Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..24] + ".img");
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length > 0) return file;
            await ImageGate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(dir);
                using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return null;
                if (resp.Content.Headers.ContentLength is > 3_000_000) return null; // miniatura enorme: ignora
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length is 0 or > 3_000_000) return null;
                var tmp = file + ".part";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, file, true);
                TrimCache(dir);
                return file;
            }
            finally { ImageGate.Release(); }
        }
        catch { return null; }
    }

    /// <summary>Mantém o cache de miniaturas em ~60 MB apagando as mais antigas.</summary>
    private static void TrimCache(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.img").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= 60L * 1024 * 1024) break;
                total -= f.Length;
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }
}
