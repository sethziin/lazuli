namespace Lazuli.Contracts;

/// <summary>Um script salvo na biblioteca.</summary>
public sealed record ScriptEntry(string Name, string Content, string Tag = "", string? Path = null);

/// <summary>Linha do console exibida na UI.</summary>
public enum LogLevel { Info, Success, Warning, Error }

public sealed record LogLine(DateTime Time, LogLevel Level, string Text);

/// <summary>Um processo do alvo (ex.: um Roblox aberto) como o backend o enxerga.</summary>
/// <param name="Pid">Id do processo.</param>
/// <param name="Title">Nome para exibir.</param>
/// <param name="Detail">Linha de detalhes (estado, memória, tempo aberto).</param>
/// <param name="Attached">true se o backend está conectado a este processo.</param>
/// <param name="Tip">Texto de ajuda/diagnóstico mostrado ao passar o mouse.</param>
public sealed record ClientInfo(int Pid, string Title, string Detail, bool Attached, string Tip = "");

/// <summary>Biblioteca de scripts exibida no painel lateral.</summary>
public interface IScriptLibrary
{
    IReadOnlyList<ScriptEntry> GetAll();
}

/// <summary>
/// Ponto de ligação da UI com qualquer backend .NET.
/// A UI só conhece esta interface; a implementação fica fora deste projeto.
/// </summary>
public interface IRunSession
{
    string StatusText { get; }

    /// <summary>true quando o backend está conectado ao alvo (pode mudar sozinho, ex.: o alvo foi fechado).</summary>
    bool IsAttached { get; }

    /// <summary>true quando o alvo (ex.: o Roblox) está aberto e dá para anexar. Backends sem alvo real deixam true.</summary>
    bool IsTargetRunning => true;

    /// <summary>Linhas que o backend gera por conta própria (saída do alvo etc.); pode vir de qualquer thread.</summary>
    event Action<LogLine>? LogReceived;

    /// <summary>Conecta ao alvo. Depois de voltar, <see cref="IsAttached"/> diz se deu certo.</summary>
    Task<IReadOnlyList<LogLine>> AttachAsync(CancellationToken ct = default);

    /// <summary>Solta a conexão do lado da UI.</summary>
    void Detach();

    Task<IReadOnlyList<LogLine>> RunAsync(string source, CancellationToken ct = default);

    /// <summary>Processos do alvo abertos agora (anexados ou não). Chamado de qualquer thread, de tempos em tempos.</summary>
    IReadOnlyList<ClientInfo> GetClients() => [];

    /// <summary>Executa só nos clientes indicados (null = todos os anexados).</summary>
    Task<IReadOnlyList<LogLine>> RunOnAsync(string source, IReadOnlyCollection<int>? pids, CancellationToken ct = default) =>
        RunAsync(source, ct);

    /// <summary>Encerra processos do alvo (null = todos). Só pode mexer no processo do alvo, nunca em outros programas.</summary>
    /// <summary>Versão do alvo que este backend suporta (no Roblox, o nome da pasta: "version-xxxxxxxxxxxxxxxx"), ou null se não sabe. Pode consultar a rede.</summary>
    Task<string?> GetSupportedTargetVersionAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    Task<IReadOnlyList<LogLine>> KillAsync(IReadOnlyCollection<int>? pids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LogLine>>([new(DateTime.Now, LogLevel.Warning, "This backend can't close the target.")]);
}
