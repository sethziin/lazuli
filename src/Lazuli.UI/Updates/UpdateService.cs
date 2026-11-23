using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lazuli.UI.Updates;

/// <summary>
/// Verifica, baixa, confere e instala atualizações.
/// Segurança: o manifesto é assinado (ECDSA P-256) e traz o SHA-256 do pacote; nada é instalado sem as duas conferências.
/// Instalação: renomeia os arquivos em uso para .old, coloca os novos e reinicia; se algo falhar, desfaz tudo.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly string StageRoot = Path.Combine(AppInfo.AppDir, ".update");
    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(6),
        }) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Lazuli/{AppInfo.VersionText}");
        _http.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
    }

    // ---------- verificar ----------

    /// <summary>Devolve a atualização (ou null se já está na última). Lança <see cref="UpdateException"/> se não deu para conferir.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        var uri = UpdateConfig.ManifestUri;
        if (!UpdateConfig.IsAllowed(uri)) throw new UpdateException("The update address must be HTTPS.");

        string json;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) throw new UpdateException($"Update server returned{(int)resp.StatusCode}.");
            if (resp.Content.Headers.ContentLength is > 64 * 1024) throw new UpdateException("Manifesto grande demais.");
            json = await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (UpdateException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException("Update check timed out."); }
        catch (HttpRequestException ex) { throw new UpdateException("Can't reach the update server.", ex); }

        return Parse(json, uri);
    }

    /// <summary>Confere assinatura, canal e versão. Devolve null se não há nada novo para este canal.</summary>
    public static UpdateInfo? Parse(string manifestJson, Uri manifestUri)
    {
        try
        {
            using var env = JsonDocument.Parse(manifestJson);
            var payload = env.RootElement.GetProperty("payload").GetString() ?? throw new UpdateException("Manifest has no content.");
            var sig = Convert.FromBase64String(env.RootElement.GetProperty("signature").GetString() ?? "");

            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateConfig.PublicKeySpki), out _);
            if (!ec.VerifyData(Encoding.UTF8.GetBytes(payload), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new UpdateException("Invalid update signature. Nothing was installed.");

            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            if (r.GetProperty("channel").GetString() != AppInfo.Channel) return null;
            if (!Version.TryParse(r.GetProperty("version").GetString(), out var ver)) throw new UpdateException("Invalid version in the manifest.");
            if (ver <= AppInfo.Version) return null;

            var mandatory = r.TryGetProperty("mandatory", out var m) && m.ValueKind == JsonValueKind.True;
            if (r.TryGetProperty("minVersion", out var mv) && mv.ValueKind == JsonValueKind.String
                && Version.TryParse(mv.GetString(), out var min) && AppInfo.Version < min) mandatory = true;

            var notes = r.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.Array
                ? n.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).Take(12).ToArray()
                : [];

            var pkg = r.GetProperty("package").GetString() ?? "";
            if (pkg.Length == 0 || pkg.Contains("..") || pkg.Contains('\\') || pkg.StartsWith('/'))
                throw new UpdateException("Invalid package name in the manifest.");
            var pkgUri = new Uri(manifestUri, pkg);
            if (!UpdateConfig.IsAllowed(pkgUri)) throw new UpdateException("The update package must be HTTPS.");

            var sha = (r.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw new UpdateException("Invalid hash in the manifest.");
            var size = r.GetProperty("size").GetInt64();
            if (size <= 0 || size > 1L << 30) throw new UpdateException("Invalid size in the manifest.");
            var released = r.TryGetProperty("released", out var rel) ? rel.GetString() ?? "" : "";

            // pacote grande: o servidor (Cloudflare) limita cada arquivo a 25 MiB, então vem em partes <pkg>.001, .002… (a soma é conferida pelo SHA-256 acima)
            var parts = r.TryGetProperty("parts", out var pp) && pp.TryGetInt32(out var pn) ? pn : 1;
            if (parts < 1 || parts > 40) throw new UpdateException("Invalid package parts in the manifest.");

            return new UpdateInfo(ver, mandatory, notes, pkgUri, sha, size, released, parts);
        }
        catch (UpdateException) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or CryptographicException)
        {
            throw new UpdateException("Invalid update manifest.", ex);
        }
    }

    // ---------- baixar ----------

    /// <summary>Baixa para a pasta .update, conferindo tamanho e SHA-256 enquanto grava. Devolve o caminho do zip.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(StageRoot);
        var final = Path.Combine(StageRoot, $"Lazuli-{AppInfo.Channel}-{info.VersionText}.zip");
        var part = final + ".part";
        TryDelete(final); TryDelete(part);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            var last = Stopwatch.StartNew();
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                for (var i = 1; i <= info.Parts; i++)
                {
                    // pacote em partes: <pkg>.001, .002… ; pacote simples: o próprio endereço
                    var uri = info.Parts == 1 ? info.PackageUri : new Uri(info.PackageUri.AbsoluteUri + $".{i:000}");
                    // cada parte tenta até 3 vezes (queda de rede no meio não derruba a atualização inteira)
                    for (var attempt = 1; ; attempt++)
                    {
                        var startDone = done;
                        var startPos = dst.Position;
                        try
                        {
                            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                            if (!resp.IsSuccessStatusCode) throw new UpdateException($"Download failed ({(int)resp.StatusCode}).");
                            var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            await using var src = await resp.Content.ReadAsStreamAsync(ct);
                            var buf = new byte[81920];
                            int n;
                            while ((n = await src.ReadAsync(buf, ct)) > 0)
                            {
                                done += n;
                                if (done > info.Size) throw new UpdateException("The downloaded file is larger than expected.");
                                partHash.AppendData(buf, 0, n);
                                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                                if (last.ElapsedMilliseconds >= 80) { progress.Report((done, info.Size)); last.Restart(); }
                            }
                            hash.AppendData(await ReadBackAsync(dst, startPos, done - startDone, ct)); // soma do total só com o que ficou gravado
                            break;
                        }
                        catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested
                                                   && (ex is HttpRequestException or IOException or TaskCanceledException || ex is UpdateException { Message: var m } && m.StartsWith("Download failed (5")))
                        {
                            // volta ao começo desta parte e tenta de novo
                            done = startDone;
                            dst.SetLength(startPos); dst.Position = startPos;
                            await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                        }
                    }
                }
            }
            progress.Report((done, info.Size));
            if (done != info.Size) throw new UpdateException("Download incomplete.");
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("Downloaded file is corrupted (hash mismatch). Nothing was installed.");
            File.Move(part, final, true);
            return final;
        }
        catch (HttpRequestException ex) { TryDelete(part); throw new UpdateException("Connection lost during download.", ex); }
        catch (IOException ex) when (ex is not FileNotFoundException) { TryDelete(part); throw new UpdateException("Couldn't save the download (disk full or no permission).", ex); }
        catch { TryDelete(part); throw; }
    }

    /// <summary>Lê de volta o trecho recém-gravado (para o hash do total: assim uma parte repetida nunca entra duas vezes).</summary>
    private static async Task<byte[]> ReadBackAsync(FileStream fs, long from, long count, CancellationToken ct)
    {
        var end = fs.Position;
        var data = new byte[count];
        fs.Position = from;
        await fs.ReadExactlyAsync(data, ct);
        fs.Position = end;
        return data;
    }
    // ---------- instalar ----------

    /// <summary>A pasta do app aceita gravação? (Program Files, por exemplo, não aceita sem administrador.)</summary>
    public static bool CanWriteToAppDir()
    {
        try
        {
            var probe = Path.Combine(AppInfo.AppDir, $".w{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Extrai o zip para uma pasta de preparo (mesmo disco) e troca os arquivos. Os que estão em uso (o próprio exe, DLLs
    /// carregadas) são renomeados para .old, o que o Windows permite. Qualquer falha desfaz tudo.
    /// </summary>
    public static void Install(string zipPath)
    {
        var appDir = AppInfo.AppDir;
        var stage = Path.Combine(StageRoot, "stage");
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var stageFull = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith('/') || e.Name.Length == 0) continue;
                    var dest = Path.GetFullPath(Path.Combine(stage, e.FullName));
                    if (!dest.StartsWith(stageFull, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("Package contains an invalid path.");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, true);
                }
            }
            if (!File.Exists(Path.Combine(stage, Path.GetFileName(AppInfo.ExePath))))
                throw new UpdateException("The package doesn't contain the app executable.");

            var swapped = new List<(string Target, string? Backup)>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(appDir, Path.GetRelativePath(stage, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string? backup = null;
                    if (File.Exists(target))
                    {
                        backup = target + ".old";
                        if (File.Exists(backup) && !TryDelete(backup)) backup = $"{target}.{Guid.NewGuid():N}.old";
                        File.Move(target, backup);
                    }
                    swapped.Add((target, backup));
                    File.Move(file, target);
                }
            }
            catch
            {
                for (var i = swapped.Count - 1; i >= 0; i--) // desfaz na ordem inversa
                {
                    var (t, b) = swapped[i];
                    try
                    {
                        if (b is null) TryDelete(t);
                        else { TryDelete(t); File.Move(b, t, true); }
                    }
                    catch { /* melhor esforço: o que não voltar fica como .old */ }
                }
                throw;
            }
        }
        catch (UpdateException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new UpdateException("Couldn't install: a file is in use or not writable.", ex);
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { /* limpeza na próxima abertura */ }
        }
    }

    /// <summary>Abre a versão nova (que espera este processo terminar). Devolve true se conseguiu iniciar.</summary>
    public static bool StartNewVersion(bool verify = true)
    {
        try
        {
            var psi = new ProcessStartInfo(AppInfo.ExePath) { UseShellExecute = true, WorkingDirectory = AppInfo.AppDir };
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            using var child = Process.Start(psi);
            if (child is null) return false;
            if (!verify) return true; // reinício comum: não espera, o chamador fecha na hora e a nova instância aguarda este processo sair
            // a versão nova espera este processo sair; se ela encerrar logo ao abrir, algo deu errado e este app NÃO deve fechar
            for (var i = 0; i < 25; i++)
            {
                if (child.HasExited) return false;
                Thread.Sleep(100);
            }
            return true;
        }
        catch { return false; }
    }

    // ---------- na abertura ----------

    /// <summary>Se este processo foi aberto por uma atualização, espera o anterior terminar (evita dois apps gravando as configurações).</summary>
    public static void WaitForPreviousInstance(string[] args)
    {
        var i = Array.IndexOf(args, "--wait-pid");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(15000); } catch { /* já saiu */ }
    }

    /// <summary>Apaga os .old e a pasta de preparo deixados pela atualização anterior, em segundo plano.</summary>
    public static void CleanupLeftovers() => _ = Task.Run(async () =>
    {
        // o exe antigo pode ficar preso por alguns segundos (processo saindo, antivírus): tenta de novo algumas vezes
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var left = Directory.EnumerateFiles(AppInfo.AppDir, "*.old", SearchOption.AllDirectories).Where(f => !TryDelete(f)).Any();
                if (Directory.Exists(StageRoot)) Directory.Delete(StageRoot, true);
                if (!left) return;
            }
            catch { /* tenta de novo */ }
            await Task.Delay(2000);
        }
    });

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; } catch { return false; }
    }

    public void Dispose() => _http.Dispose();
}
