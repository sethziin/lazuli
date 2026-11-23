using Lazuli.Contracts;

namespace Lazuli.TestFixture;

public sealed class FixtureFactory : IBackendFactory
{
    public int ContractVersion => ContractInfo.Version;

    public BackendInfo Info { get; } = new("fixture", "Fixture de teste", "0.0.1", "Alvo de teste", false,
        BackendCapabilities.ListClients | BackendCapabilities.LogStream);

    public IRunSession Create(BackendContext context) => new FixtureSession(context);
}

/// <summary>Sessão previsível: nada de processo real. Devolve textos que o teste confere.</summary>
public sealed class FixtureSession(BackendContext context) : IRunSession
{
    public BackendContext Context { get; } = context;
    public string StatusText => "fixture · " + Context.BackendDirectory;
    public bool IsAttached { get; private set; }
    public bool IsTargetRunning => true;
    public event Action<LogLine>? LogReceived;

    public Task<IReadOnlyList<LogLine>> AttachAsync(CancellationToken ct = default)
    {
        IsAttached = true;
        LogReceived?.Invoke(new LogLine(DateTime.Now, LogLevel.Info, "fixture: evento"));
        return Task.FromResult<IReadOnlyList<LogLine>>([new(DateTime.Now, LogLevel.Success, "fixture: anexado")]);
    }

    public void Detach() => IsAttached = false;

    public Task<IReadOnlyList<LogLine>> RunAsync(string source, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LogLine>>([new(DateTime.Now, LogLevel.Info, $"fixture: {source.Length} caracteres")]);

    public IReadOnlyList<ClientInfo> GetClients() => [new ClientInfo(1234, "Alvo", "fixture", IsAttached)];
}
