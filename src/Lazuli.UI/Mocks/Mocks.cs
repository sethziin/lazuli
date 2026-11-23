using Lazuli.Contracts;

namespace Lazuli.UI.Mocks;

/// <summary>Dados falsos só para a interface ter conteúdo. Não executa nada.</summary>
public sealed class MockScriptLibrary : IScriptLibrary
{
    public IReadOnlyList<ScriptEntry> GetAll() =>
    [
        new("hello.lua", "print(\"hello world\")\n", "demo"),
        new("loops.lua", "for i = 1, 5 do\n    print(i)\nend\n", "demo"),
        new("table_utils.lua", "local t = { 3, 1, 2 }\ntable.sort(t)\nprint(table.concat(t, \", \"))\n", "utils"),
        new("string_fmt.lua", "print(string.format(\"%05.2f\", 3.14159))\n", "utils"),
    ];
}

public sealed class MockRunSession : IRunSession
{
    public string StatusText => "Demo · sem backend";
    public bool IsAttached { get; private set; }
#pragma warning disable CS0067
    public event Action<LogLine>? LogReceived;
#pragma warning restore CS0067

    public async Task<IReadOnlyList<LogLine>> AttachAsync(CancellationToken ct = default)
    {
        await Task.Delay(700, ct); // simulação visual
        IsAttached = true;
        return [new(DateTime.Now, LogLevel.Success, "Attached (demo, sem backend).")];
    }

    public void Detach() => IsAttached = false;

    public async Task<IReadOnlyList<LogLine>> RunAsync(string source, CancellationToken ct = default)
    {
        await Task.Delay(250, ct);
        return
        [
            new(DateTime.Now, LogLevel.Info, $"[mock] {source.Length} caracteres recebidos"),
            new(DateTime.Now, LogLevel.Warning, "No backend connected. Demo mode."),
        ];
    }

    // ---- clientes de mentira, só para mostrar o gerenciador na demonstração ----
    private readonly object _lock = new();
    private readonly List<int> _fake = [4120, 5308];

    public IReadOnlyList<ClientInfo> GetClients()
    {
        lock (_lock)
            return _fake.Select((pid, i) => new ClientInfo(pid, "Roblox",
                (IsAttached ? "Anexado" : "Sem attach") + (i == 0 ? " · 412 MB · 12 min" : " · 388 MB · 3 min"),
                IsAttached, "Demo client.")).ToList();
    }

    public Task<IReadOnlyList<LogLine>> RunOnAsync(string source, IReadOnlyCollection<int>? pids, CancellationToken ct = default) =>
        RunAsync(source, ct);

    public Task<IReadOnlyList<LogLine>> KillAsync(IReadOnlyCollection<int>? pids, CancellationToken ct = default)
    {
        var lines = new List<LogLine>();
        lock (_lock)
        {
            var gone = pids is null ? _fake.ToList() : _fake.Where(pids.Contains).ToList();
            foreach (var p in gone) { _fake.Remove(p); lines.Add(new(DateTime.Now, LogLevel.Info, $"PID {p} encerrado (demo).")); }
            if (_fake.Count == 0) IsAttached = false;
        }
        if (lines.Count > 0) _ = Task.Delay(4000).ContinueWith(_ => { lock (_lock) if (_fake.Count == 0) _fake.Add(6000 + Random.Shared.Next(999)); });
        return Task.FromResult<IReadOnlyList<LogLine>>(lines);
    }
}
