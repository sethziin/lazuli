namespace Executor.Contracts;

/// <summary>Versão do contrato entre o app e os backends. Sobe quando <see cref="IRunSession"/> ou os tipos abaixo mudam de forma incompatível.</summary>
public static class ContractInfo
{
    public const int Version = 1;
}

/// <summary>O que um backend sabe fazer. A UI usa isto para não oferecer o que o backend não tem.</summary>
[Flags]
public enum BackendCapabilities
{
    None = 0,

    /// <summary><see cref="IRunSession.GetClients"/> devolve a lista de processos do alvo.</summary>
    ListClients = 1,

    /// <summary><see cref="IRunSession.RunOnAsync"/> respeita a lista de PIDs (sem isto, executa em todos os anexados).</summary>
    PerClientRun = 2,

    /// <summary><see cref="IRunSession.KillAsync"/> encerra processos do alvo.</summary>
    KillTarget = 4,

    /// <summary>O backend dispara <see cref="IRunSession.LogReceived"/> por conta própria.</summary>
    LogStream = 8,

    All = ListClients | PerClientRun | KillTarget | LogStream,
}

/// <summary>Descrição de um backend. Vem do código do backend (<see cref="IBackendFactory.Info"/>), não do arquivo de manifesto.</summary>
/// <param name="Id">Identificador estável, minúsculo, sem espaços (ex.: "xeno", "demo"). É o que fica salvo nas configurações.</param>
/// <param name="DisplayName">Nome mostrado ao usuário.</param>
/// <param name="Version">Versão do backend (texto livre).</param>
/// <param name="TargetName">Nome do alvo que o backend controla (ex.: "Roblox").</param>
/// <param name="RequiresAdmin">true se o backend só funciona com o app elevado.</param>
/// <param name="Capabilities">Recursos disponíveis.</param>
public sealed record BackendInfo(
    string Id,
    string DisplayName,
    string Version,
    string TargetName,
    bool RequiresAdmin,
    BackendCapabilities Capabilities)
{
    /// <summary>Id reservado do backend de demonstração embutido no app (nunca vem de um plugin).</summary>
    public const string DemoId = "demo";

    /// <summary>O backend de demonstração (MockRunSession): sempre existe, é o plano B quando um plugin não carrega.</summary>
    public static BackendInfo Demo { get; } = new(DemoId, "Demonstração", "1.0", "Roblox", false, BackendCapabilities.All);

    public bool Has(BackendCapabilities c) => (Capabilities & c) == c;
}

/// <summary>Dados que o app entrega ao backend ao criá-lo.</summary>
/// <param name="BackendDirectory">Pasta do backend (onde estão a DLL dele e os arquivos nativos). Nunca use AppContext.BaseDirectory.</param>
/// <param name="DataDirectory">Pasta onde o backend pode gravar dados próprios.</param>
/// <param name="HostVersion">Versão do app.</param>
public sealed record BackendContext(string BackendDirectory, string DataDirectory, Version HostVersion);

/// <summary>
/// Ponto de entrada de um backend. Cada backend (plugin) expõe exatamente um tipo público, sem parâmetros no construtor,
/// que implementa esta interface; o manifesto <c>backend.json</c> aponta para ele.
/// </summary>
public interface IBackendFactory
{
    BackendInfo Info { get; }

    /// <summary>Deve devolver <see cref="ContractInfo.Version"/> com o qual o backend foi compilado.</summary>
    int ContractVersion { get; }

    /// <summary>Cria a sessão. Não deve carregar DLLs nativas nem tocar no alvo: isso fica para o primeiro Attach.</summary>
    IRunSession Create(BackendContext context);
}
