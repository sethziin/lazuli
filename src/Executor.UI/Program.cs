using Avalonia;
using Avalonia.Win32;
using Executor.UI.Services;

namespace Executor.UI;

internal static class Program
{
    private static AppSettings? _settings;

    /// <summary>Configurações lidas uma vez, cedo: o modo de desempenho decide o renderizador antes de iniciar o Avalonia.</summary>
    public static AppSettings EarlySettings => _settings ??= Storage.LoadSettings();

    /// <summary>true quando o app está usando renderização por software (modo leve).</summary>
    public static bool UsingSoftware { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // exe de lançamento aberto fora da pasta de instalação: instala em %LOCALAPPDATA%\Programs\Lazuli e abre a cópia instalada
        if (Installer.TryRelocate(args)) return;
        // o que o app precisa e o Windows pode não ter (VC++ Runtime): oferece baixar o oficial da Microsoft; sem ele a janela nem abre
        if (!Prerequisites.EnsureReady()) return;
        CrashLog.Hook();
        CrashLog.Trace($"início pid={Environment.ProcessId} args=[{string.Join(' ', args)}]");
        // aberto por uma atualização: espera o processo anterior terminar antes de mexer em configurações/arquivos
        Updates.UpdateService.WaitForPreviousInstance(args);
        CrashLog.Trace("espera do processo anterior terminou");
        if (!SingleInstance.TryAcquire(waitForPrevious: args.Contains("--wait-pid"))) { CrashLog.Trace("já havia outra instância: saindo"); return; } // a janela existente veio para a frente
        CrashLog.Trace("instância única adquirida");
        if (RelaunchElevatedIfNeeded(args)) { CrashLog.Trace("relançado como administrador: saindo"); return; }
        Updates.UpdateService.CleanupLeftovers();
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex)
        {
            CrashLog.Write("inicialização", ex);
            throw;
        }
    }

    /// <summary>
    /// Se o backend escolhido exige administrador e o app não está elevado, abre outra cópia elevada (um pedido do UAC) e este processo sai.
    /// Recusar o UAC não impede de abrir: o app segue sem elevação e o próprio backend avisa que falta permissão.
    /// </summary>
    private static bool RelaunchElevatedIfNeeded(string[] args)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("EXECUTOR_SHOT") is { Length: > 0 }
                || Environment.GetEnvironmentVariable("EXECUTOR_SELFTEST") is { Length: > 0 }
                || Environment.GetEnvironmentVariable("EXECUTOR_EDITTEST") == "1") return false;
            var pick = Backends.BackendHost.Pick(EarlySettings.BackendId, Backends.BackendCatalog.Discover(Backends.BackendCatalog.DefaultRoot), []);
            if (pick is null) return false;
            var again = args.Contains(Backends.ElevationGate.ElevatedFlag);
            if (!Backends.ElevationGate.ShouldRelaunch(pick.Manifest.RequiresAdmin, Backends.ElevationGate.IsAdmin(), again)) return false;
            CrashLog.Trace($"backend '{pick.Manifest.Id}' exige administrador: relançando elevado");
            return Backends.ElevationGate.TryRelaunchElevated(Updates.AppInfo.ExePath, Updates.AppInfo.AppDir, args);
        }
        catch (Exception ex) { CrashLog.Trace("relançar elevado falhou: " + ex.Message); return false; }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();

        // EXECUTOR_RENDER=software|gpu força o modo (testes); senão vale o modo de desempenho das configurações.
        var forced = Environment.GetEnvironmentVariable("EXECUTOR_RENDER");
        UsingSoftware = forced == "software" || (forced != "gpu" && Perf.IsLite(EarlySettings));
        if (UsingSoftware)
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        return builder;
    }
}
