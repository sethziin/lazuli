using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Lazuli.UI.Services;

/// <summary>
/// Baixa uma versão específica do Roblox (WindowsPlayer, canal LIVE) direto da CDN oficial do Roblox, do mesmo jeito que o
/// RDD (Roblox Deployment Downloader, licença MIT): lê <c>{versão}-rbxPkgManifest.txt</c>, baixa cada .zip, confere o MD5
/// do manifesto e extrai cada pacote na subpasta certa. Fica numa pasta do Lazuli (%LOCALAPPDATA%\Lazuli\roblox\version-x),
/// sem tocar no Roblox instalado. Só guarda uma versão: instalar outra apaga a anterior.
/// </summary>
public static partial class RobloxDeployer
{
    public const string PlayerExe = "RobloxPlayerBeta.exe";
    private const string ProcessName = "RobloxPlayerBeta";

    private static readonly string[] Hosts =
    [
        "https://setup.rbxcdn.com",
        "https://setup-aws.rbxcdn.com",
        "https://setup-ak.rbxcdn.com",
        "https://roblox-setup.cachefly.net",
        "https://s3.amazonaws.com/setup.roblox.com",
    ];

    /// <summary>Pacote do manifesto → subpasta onde ele é extraído (WindowsPlayer).</summary>
    private static readonly Dictionary<string, string> ExtractRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RobloxApp.zip"] = "",
        ["redist.zip"] = "",
        ["WebView2.zip"] = "",
        ["shaders.zip"] = "shaders/",
        ["ssl.zip"] = "ssl/",
        ["WebView2RuntimeInstaller.zip"] = "WebView2RuntimeInstaller/",
        ["content-avatar.zip"] = "content/avatar/",
        ["content-configs.zip"] = "content/configs/",
        ["content-fonts.zip"] = "content/fonts/",
        ["content-sky.zip"] = "content/sky/",
        ["content-sounds.zip"] = "content/sounds/",
        ["content-textures2.zip"] = "content/textures/",
        ["content-models.zip"] = "content/models/",
        ["content-platform-fonts.zip"] = "PlatformContent/pc/fonts/",
        ["content-platform-dictionaries.zip"] = "PlatformContent/pc/shared_compression_dictionaries/",
        ["content-terrain.zip"] = "PlatformContent/pc/terrain/",
        ["content-textures3.zip"] = "PlatformContent/pc/textures/",
        ["extracontent-luapackages.zip"] = "ExtraContent/LuaPackages/",
        ["extracontent-translations.zip"] = "ExtraContent/translations/",
        ["extracontent-models.zip"] = "ExtraContent/models/",
        ["extracontent-textures.zip"] = "ExtraContent/textures/",
        ["extracontent-places.zip"] = "ExtraContent/places/",
    };

    private const string AppSettingsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Settings>\r\n\t<ContentFolder>content</ContentFolder>\r\n\t<BaseUrl>http://www.roblox.com</BaseUrl>\r\n</Settings>\r\n";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    [GeneratedRegex("^version-[0-9a-f]{16}$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRx();

    public static bool IsValidVersion(string? v) => v is not null && VersionRx().IsMatch(v);

    public static string Root => Path.Combine(Storage.LocalRoot, "roblox");

    public static string PlayerPath(string version) => Path.Combine(Root, version, PlayerExe);

    public static bool IsInstalled(string version) => IsValidVersion(version) && File.Exists(PlayerPath(version));

    /// <summary>Versões (nome da pasta) dos Roblox abertos agora. Vazio se nenhum está aberto ou não deu para ler.</summary>
    public static IReadOnlyList<string> RunningVersions()
    {
        var list = new List<string>();
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                try
                {
                    var folder = Path.GetFileName(Path.GetDirectoryName(p.MainModule?.FileName));
                    if (IsValidVersion(folder) && !list.Contains(folder!, StringComparer.OrdinalIgnoreCase)) list.Add(folder!);
                }
                catch { /* processo fechou ou sem acesso */ }
            }
        }
        return list;
    }

    public sealed record Package(string Name, string Md5, long Size);

    /// <summary>Lê o manifesto de pacotes: "v0" e depois grupos de 4 linhas (nome, md5, tamanho compactado, tamanho real).</summary>
    public static List<Package> ParseManifest(string text)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        if (lines.Count == 0 || lines[0] != "v0") throw new InvalidDataException("Unexpected package manifest format.");
        var list = new List<Package>();
        for (var i = 1; i + 3 < lines.Count; i += 4)
        {
            if (!long.TryParse(lines[i + 2], out var size)) throw new InvalidDataException($"Bad size for {lines[i]}.");
            list.Add(new(lines[i], lines[i + 1].ToLowerInvariant(), size));
        }
        return list;
    }

    private static async Task<(string Host, string Text)> FetchManifestAsync(string version, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var h in Hosts)
        {
            try
            {
                using var resp = await Http.GetAsync($"{h}/{version}-rbxPkgManifest.txt", ct);
                if (!resp.IsSuccessStatusCode) { last = new HttpRequestException($"{(int)resp.StatusCode}"); continue; }
                return (h, await resp.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { last = ex; }
        }
        throw new InvalidOperationException($"Couldn't find {version} on Roblox's servers ({last?.Message}).");
    }

    /// <summary>
    /// Baixa e instala a versão. progress recebe (texto, fração 0..1). Apaga as outras versões baixadas pelo Lazuli no final.
    /// </summary>
    public static async Task InstallAsync(string version, IProgress<(string Text, double Fraction)>? progress, CancellationToken ct)
    {
        if (!IsValidVersion(version)) throw new ArgumentException("Invalid Roblox version.", nameof(version));
        version = version.ToLowerInvariant();
        progress?.Report(("Reading package list…", 0));
        var (host, text) = await FetchManifestAsync(version, ct);
        var packages = ParseManifest(text);
        if (!packages.Any(p => p.Name.Equals("RobloxApp.zip", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{version} isn't a Roblox Player build.");
        var zips = packages.Where(p => ExtractRoots.ContainsKey(p.Name)).ToList();

        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, version);
        var work = target + ".part";
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var workFull = Path.GetFullPath(work) + Path.DirectorySeparatorChar;
        var dl = Path.Combine(Root, version + ".download"); // zips baixados (fora da pasta final; apagados um a um)
        if (Directory.Exists(dl)) Directory.Delete(dl, true);
        Directory.CreateDirectory(dl);

        var total = Math.Max(1, zips.Sum(z => z.Size));
        long done = 0;
        var gate = new SemaphoreSlim(4);
        var extractLock = new object();
        try
        {
            var tasks = zips.Select(async pkg =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    // baixa para um arquivo (o RobloxApp.zip passa de 100 MB), confere o MD5 e extrai dele
                    var tmp = Path.Combine(dl, pkg.Name);
                    using (var resp = await Http.GetAsync($"{host}/{version}-{pkg.Name}", HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var fs = File.Create(tmp);
                        await resp.Content.CopyToAsync(fs, ct);
                    }
                    string md5;
                    await using (var fs = File.OpenRead(tmp)) md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(fs, ct));
                    if (md5 != pkg.Md5) throw new InvalidDataException($"{pkg.Name} is corrupted (MD5 doesn't match).");
                    var root = ExtractRoots[pkg.Name];
                    using var zip = ZipFile.OpenRead(tmp);
                    foreach (var e in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue; // pasta
                        var rel = (root + e.FullName.Replace('\\', '/')).TrimStart('/');
                        var dest = Path.GetFullPath(Path.Combine(work, rel));
                        if (!dest.StartsWith(workFull, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"{pkg.Name} has an unsafe path.");
                        lock (extractLock)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            e.ExtractToFile(dest, true);
                        }
                    }
                    zip.Dispose();
                    try { File.Delete(tmp); } catch { }
                    var now = Interlocked.Add(ref done, pkg.Size);
                    progress?.Report(($"Downloading… {now / 1048576.0:0} of {total / 1048576.0:0} MB", (double)now / total));
                }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);

            await File.WriteAllTextAsync(Path.Combine(work, "AppSettings.xml"), AppSettingsXml, ct);
            if (!File.Exists(Path.Combine(work, PlayerExe))) throw new InvalidDataException($"{PlayerExe} missing from the download.");

            progress?.Report(("Finishing…", 1));
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(work, target);
        }
        catch
        {
            try { Directory.Delete(work, true); } catch { }
            throw;
        }
        finally { try { Directory.Delete(dl, true); } catch { } }
        RemoveOthers(version);
    }

    /// <summary>Apaga as versões baixadas pelo Lazuli que não são esta (só dentro da pasta do Lazuli; nunca o Roblox instalado).</summary>
    public static void RemoveOthers(string keep)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(d);
                if (name.Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsValidVersion(name) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(d, true); } catch { /* em uso: fica para a próxima */ }
            }
        }
        catch { }
    }

    /// <summary>Abre o app do Roblox nessa versão (sem link de jogo: o jogo é escolhido dentro do Roblox).</summary>
    public static void Launch(string version)
    {
        var exe = PlayerPath(version);
        Process.Start(new ProcessStartInfo(exe, "--app") { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false })?.Dispose();
    }

    /// <summary>Fecha os Roblox abertos (só o processo do Roblox).</summary>
    public static async Task CloseRunningAsync()
    {
        foreach (var p in Process.GetProcessesByName(ProcessName))
            using (p) { try { p.Kill(); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
    }
}
