using Executor.Contracts;

namespace Executor.SolaraBackend;

/// <summary>Ponto de entrada do backend Solara (apontado por "factory" em backend.json).</summary>
public sealed class SolaraFactory : IBackendFactory
{
    public int ContractVersion => ContractInfo.Version;

    public BackendInfo Info { get; } = new(
        Id: "solara",
        DisplayName: "Solara",
        Version: "3.2.22",
        TargetName: "Roblox",
        RequiresAdmin: false,
        Capabilities: BackendCapabilities.ListClients | BackendCapabilities.PerClientRun |
                      BackendCapabilities.KillTarget | BackendCapabilities.LogStream);

    // Só cria o objeto: a SolaraV3.dll é carregada (e o Start chamado) no primeiro Attach.
    public IRunSession Create(BackendContext context) => new SolaraRunSession(context.BackendDirectory, context.DataDirectory);
}
