using System.Reflection;

namespace Executor.UI.Updates;

/// <summary>Versão e variante deste exe. A versão vem do &lt;Version&gt; do csproj (o script de publicação a define).</summary>
public static class AppInfo
{
    public static Version Version { get; } = ReadVersion();

    public static string VersionText => Version.Build >= 0 ? $"{Version.Major}.{Version.Minor}.{Version.Build}" : Version.ToString();

    /// <summary>Canal único: todos os builds, com qualquer backend, recebem as atualizações por aqui.</summary>
    public const string Channel = "main";

    /// <summary>Nome do backend em uso (escolhido no início do app; veja Backends\BackendHost).</summary>
    public static string BackendName => Backends.BackendHost.Current.Id == Contracts.BackendInfo.DemoId
        ? "None (demo mode)"
        : $"{Backends.BackendHost.Current.DisplayName} {Backends.BackendHost.Current.Version}";

    /// <summary>Pasta onde o exe está (e onde a atualização troca os arquivos).</summary>
    public static string AppDir { get; } = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Lazuli.exe");

    private static Version ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info is not null)
        {
            var clean = info.Split('+', '-')[0];
            if (Version.TryParse(clean, out var v)) return v;
        }
        return typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);
    }
}
