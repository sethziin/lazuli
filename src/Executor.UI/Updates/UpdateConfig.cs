namespace Executor.UI.Updates;

/// <summary>Para onde olhar e em quem confiar. A chave PRIVADA nunca entra no app (fica em scripts\keys).</summary>
public static class UpdateConfig
{
    /// <summary>
    /// Endereço base da hospedagem (Cloudflare Pages), ex.: "https://executor-updates.pages.dev".
    /// O app lê &lt;base&gt;/&lt;canal&gt;/latest.json. Vazio = atualização automática desligada.
    /// </summary>
    public const string DefaultBaseUrl = "https://executor-updates.lpn688c.workers.dev";

    /// <summary>Chave pública ECDSA P-256 (SPKI, base64) que confere a assinatura do manifesto.</summary>
    public const string PublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEEcwYeT7odNExpFG/adeTwttNd5TXc52baK0OhcA7pLaP0wmJDgbHG/4ucwEsPXopk4GTTS72XneVaf1637AmBg==";

    /// <summary>EXECUTOR_UPDATE_URL troca a base (testes). A assinatura continua sendo exigida.</summary>
    public static string BaseUrl =>
        (Environment.GetEnvironmentVariable("EXECUTOR_UPDATE_URL") is { Length: > 0 } e ? e : DefaultBaseUrl).Trim().TrimEnd('/');

    public static bool Enabled =>
        BaseUrl.Length > 0 && Environment.GetEnvironmentVariable("EXECUTOR_NOUPDATE") != "1";

    public static Uri ManifestUri => new($"{BaseUrl}/{AppInfo.Channel}/latest.json");

    /// <summary>Só HTTPS; HTTP apenas para a própria máquina (testes locais).</summary>
    public static bool IsAllowed(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);
}
