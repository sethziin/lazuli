using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lazuli.UI.Backends;

/// <summary>
/// Conteúdo de <c>backends\&lt;pasta&gt;\backend.json</c>: o que o app lê <b>antes</b> de carregar qualquer código do backend.
/// Os hashes em <see cref="Files"/> servem só para <b>integridade</b> (arquivo corrompido ou trocado por engano);
/// não autenticam quem criou o backend. Veja <c>backends\README.md</c>.
/// </summary>
public sealed record BackendManifest(
    string Id,
    string DisplayName,
    string Assembly,
    string Factory,
    int ContractVersion,
    bool RequiresAdmin,
    IReadOnlyDictionary<string, string> Files)
{
    public const string FileName = "backend.json";

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex HashPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    /// <summary>Caminho relativo seguro: sem raiz, sem "..", sem letra de unidade, sem fluxo alternativo.</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':')) return false;
        return path.Replace('\\', '/').Split('/').All(p => p.Length > 0 && p != "." && p != "..");
    }

    /// <summary>Lê e valida o manifesto. Lança <see cref="FormatException"/> com uma mensagem que a UI pode mostrar.</summary>
    public static BackendManifest Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException($"Invalid {FileName}: {ex.Message}"); }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatException($"Invalid {FileName}: expected an object.");

            string Str(string name)
            {
                if (!r.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.GetString()))
                    throw new FormatException($"{FileName}: falta o campo de texto \"{name}\".");
                return p.GetString()!;
            }

            var id = Str("id");
            if (!IsValidId(id)) throw new FormatException($"{FileName}: invalid id \"{id}\" (use a-z, 0-9, '-' or '_', up to 32 characters).");
            if (id == Contracts.BackendInfo.DemoId) throw new FormatException($"{FileName}: the id \"{id}\" is reserved for the built-in backend.");
            var display = Str("displayName");
            var assembly = Str("assembly");
            var factory = Str("factory");
            if (!IsSafeRelativePath(assembly)) throw new FormatException($"{FileName}: caminho inseguro em \"assembly\".");

            if (!r.TryGetProperty("contractVersion", out var cv) || cv.ValueKind != JsonValueKind.Number || !cv.TryGetInt32(out var contract))
                throw new FormatException($"{FileName}: missing the \"contractVersion\" number.");
            var admin = r.TryGetProperty("requiresAdmin", out var ra) && ra.ValueKind == JsonValueKind.True;

            if (!r.TryGetProperty("files", out var f) || f.ValueKind != JsonValueKind.Object)
                throw new FormatException($"{FileName}: missing the \"files\" object (file → SHA-256).");
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in f.EnumerateObject())
            {
                var rel = p.Name.Replace('\\', '/');
                if (!IsSafeRelativePath(rel)) throw new FormatException($"{FileName}: caminho inseguro em \"files\": {p.Name}");
                var hash = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                if (hash is null || !HashPattern.IsMatch(hash)) throw new FormatException($"{FileName}: invalid SHA-256 for {p.Name}.");
                if (!files.TryAdd(rel, hash)) throw new FormatException($"{FileName}: duplicate file in \"files\": {p.Name}");
            }
            if (!files.ContainsKey(assembly.Replace('\\', '/'))) throw new FormatException($"{FileName}: \"assembly\" isn't listed in \"files\".");

            return new BackendManifest(id, display, assembly.Replace('\\', '/'), factory, contract, admin, files);
        }
    }
}
