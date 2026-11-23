using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// uso:
//   UpdateTool keygen <pastaDasChaves> [--force]
//   UpdateTool pack --dir <publish> --out <dist> --version 1.2.3 --channel main --key <update-private.pem>
//                   [--notes "a;b;c"] [--mandatory] [--min-version 1.0.0] [--keep 3]
return args.Length == 0 ? Fail("Use: keygen | pack") : args[0] switch
{
    "keygen" => KeyGen(args),
    "pack" => Pack(args),
    _ => Fail("Comando desconhecido: " + args[0]),
};

static int Fail(string msg) { Console.Error.WriteLine("ERRO: " + msg); return 1; }

static string? Opt(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static int KeyGen(string[] a)
{
    if (a.Length < 2) return Fail("keygen <pasta>");
    var dir = Path.GetFullPath(a[1]);
    var priv = Path.Combine(dir, "update-private.pem");
    if (File.Exists(priv) && !a.Contains("--force"))
        return Fail($"Já existe {priv}. Use --force só se tiver certeza: invalida a atualização automática dos exes já enviados.");
    Directory.CreateDirectory(dir);
    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(priv, ec.ExportPkcs8PrivateKeyPem());
    var pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
    File.WriteAllText(Path.Combine(dir, "update-public.txt"), pub);
    Console.WriteLine("Chave privada (SEGREDO, faça backup): " + priv);
    Console.WriteLine("PublicKeySpki = " + pub);
    return 0;
}

static int Pack(string[] a)
{
    var dir = Opt(a, "--dir"); var outDir = Opt(a, "--out"); var ver = Opt(a, "--version");
    var channel = Opt(a, "--channel"); var keyFile = Opt(a, "--key");
    if (dir is null || outDir is null || ver is null || channel is null || keyFile is null)
        return Fail("pack precisa de --dir --out --version --channel --key");
    if (!Version.TryParse(ver, out var v)) return Fail("versão inválida: " + ver);
    dir = Path.GetFullPath(dir); outDir = Path.GetFullPath(outDir);
    var exe = Directory.GetFiles(dir, "Lazuli.exe").FirstOrDefault();
    if (exe is null) return Fail("Lazuli.exe não está em " + dir);

    // zip: só arquivos do app (sem logs, .pdb, restos de atualização)
    var chDir = Path.Combine(outDir, channel);
    var pkgName = $"Lazuli-{channel}-{ver}.zip";
    var pkgPath = Path.Combine(chDir, pkgName);
    Directory.CreateDirectory(chDir);
    if (File.Exists(pkgPath)) File.Delete(pkgPath);
    var n = 0;
    using (var zip = ZipFile.Open(pkgPath, ZipArchiveMode.Create))
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.EndsWith(".log") || rel.EndsWith(".pdb") || rel.EndsWith(".old") || rel.StartsWith(".update/")) continue;
            zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
            n++;
        }
    }
    var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pkgPath))).ToLowerInvariant();
    var size = new FileInfo(pkgPath).Length;

    // o Cloudflare Workers limita cada arquivo estático a 25 MiB: pacote maior vira partes de 20 MiB (.001, .002…) e o zip inteiro sai da pasta.
    // O app baixa as partes em ordem e confere o SHA-256 do total (acima).
    const int PartSize = 20 * 1024 * 1024;
    var parts = 1;
    if (size > PartSize)
    {
        parts = (int)((size + PartSize - 1) / PartSize);
        using (var src = File.OpenRead(pkgPath))
        {
            var buf = new byte[1024 * 1024];
            for (var i = 1; i <= parts; i++)
            {
                using var dst = File.Create($"{pkgPath}.{i:000}");
                long left = Math.Min(PartSize, size - (long)(i - 1) * PartSize);
                while (left > 0) { var n2 = src.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (n2 <= 0) break; dst.Write(buf, 0, n2); left -= n2; }
            }
        }
        File.Delete(pkgPath);
    }

    var notes = (Opt(a, "--notes") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var payload = JsonSerializer.Serialize(new
    {
        version = ver,
        channel,
        mandatory = a.Contains("--mandatory"),
        minVersion = Opt(a, "--min-version"),
        notes,
        // --package-base: o zip fica em outro endereço HTTPS (ex.: R2 do Cloudflare, o Pages limita cada arquivo a 25 MiB)
        package = Opt(a, "--package-base") is { Length: > 0 } pb ? pb.TrimEnd('/') + "/" + pkgName : pkgName,
        sha256 = sha,
        size,
        parts,
        released = DateTime.UtcNow.ToString("yyyy-MM-dd"),
    });

    using var ec = ECDsa.Create();
    ec.ImportFromPem(File.ReadAllText(keyFile));
    var sig = ec.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    var manifest = JsonSerializer.Serialize(new { payload, signature = Convert.ToBase64String(sig) }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(chDir, "latest.json"), manifest);

    Console.WriteLine($"{n} arquivos -> {pkgPath}" + (parts > 1 ? $" (em {parts} partes de até 20 MiB)" : ""));
    Console.WriteLine($"tamanho {size / 1048576.0:0.0} MB · sha256 {sha}");
    Console.WriteLine("manifesto: " + Path.Combine(chDir, "latest.json"));
    return 0;
}
