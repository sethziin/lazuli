using System.Net;
using System.Text;
using System.Text.Json;

namespace Executor.SolaraBackend;

internal class SolaraException(string message) : Exception(message);

/// <summary>A ponte respondeu 401 com corpo "B" (ou Start devolveu 2): o Solara considera este usuário banido.</summary>
internal sealed class SolaraBannedException() : SolaraException("Solara refused the session: this PC/account is banned by Solara.");

/// <summary>Um cliente do Roblox como a ponte o descreve em scan_clients (status 0 = livre, 1 = no jogo, 2 = anexado esperando o jogo).</summary>
internal sealed record SolaraClient(int Id, string Username, int Status)
{
    public bool Attached => Status is 1 or 2;
    public bool Ready => Status == 1;
}

/// <summary>
/// Cliente da ponte HTTP local da DLL (Crow em 127.0.0.1:porta), com o header GUID.
/// Mesmos quatro corpos que o launcher oficial envia (07A §3, 07B §2) e a mesma leitura das respostas (07A §2).
/// </summary>
internal sealed class SolaraBridge(int port, string guid) : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(20),
    };

    public int Port => port;

    public async Task<string> PostAsync(string endpoint, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("GUID", guid);

        HttpResponseMessage resp;
        SolaraNative.Trace($"POST {endpoint} {(endpoint == "/execute_script" ? "(script)" : JsonSerializer.Serialize(body))}");
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException) { throw new SolaraException("Couldn't reach the local Solara bridge."); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            SolaraNative.Trace($"  -> {(int)resp.StatusCode} {(text.Length > 300 ? text[..300] + "…" : text)}");
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (text == "B") throw new SolaraBannedException();
                throw new SolaraException("Solara bridge refused the session (401). Restart the app.");
            }
            if (!resp.IsSuccessStatusCode)
                throw new SolaraException($"Solara respondeu {(int)resp.StatusCode}{(text.Length > 0 ? ": " + text : "")}");
            return text;
        }
    }

    public async Task<List<SolaraClient>> ScanAsync(CancellationToken ct) =>
        ParseClients(await PostAsync("/client_manager", new { request = "scan_clients" }, ct));

    /// <summary>Pede o attach. Devolve o campo "raw" da resposta (1 caractere: '^' sem acesso à versão, '>' versão não suportada) ou null.</summary>
    public async Task<string?> AttachAsync(int clientId, CancellationToken ct)
    {
        var text = await PostAsync("/request", new { request = "Attach", client_id = clientId }, ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.String)
                return raw.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public Task<string> ExecuteAsync(int clientId, string script, CancellationToken ct) =>
        PostAsync("/execute_script", new { script, client_id = clientId }, ct);

    /// <summary>Resposta de scan_clients: objeto cujas chaves são os client_id; o valor é um objeto ou um array (usa o 1º).</summary>
    internal static List<SolaraClient> ParseClients(string text)
    {
        var list = new List<SolaraClient>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(p.Name, out var id)) continue;
            var v = p.Value;
            if (v.ValueKind == JsonValueKind.Array) v = v.GetArrayLength() > 0 ? v[0] : default;
            var name = "";
            var status = 0;
            if (v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String) name = u.GetString() ?? "";
                if (v.TryGetProperty("status", out var s))
                {
                    if (s.ValueKind == JsonValueKind.Number) s.TryGetInt32(out status);
                    else if (s.ValueKind == JsonValueKind.String) int.TryParse(s.GetString(), out status);
                }
            }
            list.Add(new SolaraClient(id, name, status));
        }
        return list;
    }

    public void Dispose() => _http.Dispose();
}
