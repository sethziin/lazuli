using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Executor.UI.Services;

/// <summary>Uma entrada num servidor, lida do log do Roblox.</summary>
public sealed record RobloxJoin(DateTime Time, long PlaceId, string JobId, long? UniverseId);

/// <summary>Nome, criador e ícone de um jogo (API pública do Roblox, com cache em disco).</summary>
public sealed record RobloxGameInfo(long UniverseId, string Name, string Creator, long Playing, string? IconPath);

/// <summary>
/// Jogos recentes do Roblox, lidos dos logs locais (%LOCALAPPDATA%\Roblox\logs): cada entrada num servidor gera
/// "! Joining game '&lt;jobId&gt;' place &lt;placeId&gt;" e "game_join_loadtime: placeid:…, universeid:…".
/// Nada é enviado a lugar nenhum para isso; só o nome/ícone do jogo vêm da API pública do Roblox (por id do jogo).
/// </summary>
public static partial class RobloxRecent
{
    [GeneratedRegex(@"^(?<ts>\S+?),.*! Joining game '(?<job>[0-9a-fA-F-]{36})' place (?<place>\d+)")]
    private static partial Regex JoinLine();

    [GeneratedRegex(@"placeid:(?<place>\d+),.*?universeid:(?<uni>\d+)")]
    private static partial Regex UniverseLine();

    private static string LogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

    /// <summary>Entradas mais recentes primeiro (só as do Player), no máximo <paramref name="max"/> logs lidos.</summary>
    public static List<RobloxJoin> ReadJoins(int max = 25)
    {
        var joins = new List<RobloxJoin>();
        if (!Directory.Exists(LogDir)) return joins;
        var universes = new Dictionary<long, long>();
        var files = new DirectoryInfo(LogDir).GetFiles("*_Player_*.log")
            .Where(f => !f.Name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(max);
        foreach (var f in files)
        {
            try
            {
                // o Roblox mantém o log aberto: leitura compartilhada
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                while (sr.ReadLine() is { } line)
                {
                    if (line.Contains("! Joining game", StringComparison.Ordinal))
                    {
                        var m = JoinLine().Match(line);
                        if (!m.Success || !long.TryParse(m.Groups["place"].Value, out var place)) continue;
                        var time = DateTime.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t.ToLocalTime() : f.LastWriteTime;
                        joins.Add(new RobloxJoin(time, place, m.Groups["job"].Value, null));
                    }
                    else if (line.Contains("game_join_loadtime", StringComparison.Ordinal))
                    {
                        var m = UniverseLine().Match(line);
                        if (m.Success && long.TryParse(m.Groups["place"].Value, out var p) && long.TryParse(m.Groups["uni"].Value, out var u))
                            universes[p] = u;
                    }
                }
            }
            catch { /* log em uso/apagado: pula */ }
        }
        return joins
            .Select(j => j with { UniverseId = universes.TryGetValue(j.PlaceId, out var u) ? u : null })
            .OrderByDescending(j => j.Time).ToList();
    }

    /// <summary>Abre o Roblox (tela inicial do app).</summary>
    public static void OpenRoblox() => Shell("roblox://");

    /// <summary>Entra no jogo; com <paramref name="jobId"/>, tenta o mesmo servidor (se ainda existir e tiver vaga).</summary>
    public static void Join(long placeId, string? jobId = null) =>
        Shell(jobId is { Length: > 0 }
            ? $"roblox://experiences/start?placeId={placeId}&gameInstanceId={jobId}"
            : $"roblox://experiences/start?placeId={placeId}");

    private static void Shell(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
}

/// <summary>Busca nome/criador/jogadores e o ícone de um universo. Cache: dados por 1 h, ícone por 7 dias.</summary>
public static class RobloxGames
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return h;
    }

    private static string Dir => Path.Combine(Storage.CacheDir, "games");

    public static async Task<long?> UniverseOfAsync(long placeId, CancellationToken ct)
    {
        var file = Path.Combine(Dir, $"place-{placeId}.txt");
        if (File.Exists(file) && long.TryParse(await File.ReadAllTextAsync(file, ct), out var cached)) return cached;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe", ct));
            if (doc.RootElement.TryGetProperty("universeId", out var u) && u.TryGetInt64(out var id))
            {
                Directory.CreateDirectory(Dir);
                await File.WriteAllTextAsync(file, id.ToString(), ct);
                return id;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return null;
    }

    public static async Task<RobloxGameInfo?> GetAsync(long universeId, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var json = Path.Combine(Dir, $"{universeId}.json");
        var icon = Path.Combine(Dir, $"{universeId}.png");

        RobloxGameInfo? info = null;
        if (File.Exists(json))
            try { info = JsonSerializer.Deserialize<RobloxGameInfo>(await File.ReadAllTextAsync(json, ct)); } catch { }

        if (info is null || File.GetLastWriteTimeUtc(json) < DateTime.UtcNow.AddHours(-1))
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://games.roblox.com/v1/games?universeIds={universeId}", ct));
                var g = doc.RootElement.GetProperty("data")[0];
                info = new RobloxGameInfo(universeId,
                    g.GetProperty("name").GetString() ?? "",
                    g.TryGetProperty("creator", out var c) && c.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "",
                    g.TryGetProperty("playing", out var pl) && pl.TryGetInt64(out var n) ? n : 0,
                    null);
                await File.WriteAllTextAsync(json, JsonSerializer.Serialize(info), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* sem rede: fica com o cache, se houver */ }
        }
        if (info is null) return null;

        if (!File.Exists(icon) || File.GetLastWriteTimeUtc(icon) < DateTime.UtcNow.AddDays(-7))
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                    $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&size=256x256&format=Png&isCircular=false", ct));
                var url = doc.RootElement.GetProperty("data")[0].GetProperty("imageUrl").GetString();
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = await Http.GetByteArrayAsync(u, ct);
                    await File.WriteAllBytesAsync(icon, bytes, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return info with { IconPath = File.Exists(icon) ? icon : null };
    }
}
