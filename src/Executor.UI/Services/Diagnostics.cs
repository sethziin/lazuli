using System.Diagnostics;
using System.Runtime.InteropServices;
using Executor.UI.Updates;

namespace Executor.UI.Services;

/// <summary>Registro de erros inesperados em %LOCALAPPDATA%\Lazuli\logs\error.log (para pedir o arquivo a quem teve problema).</summary>
public static class CrashLog
{
    private static readonly string Dir = Storage.LogDir;
    private static readonly object Lock = new();

    public static string FilePath => Path.Combine(Dir, "error.log");

    public static void Write(string where, Exception? ex)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                var f = FilePath;
                if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Delete(f); // teto: não cresce sem limite
                File.AppendAllText(f, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{AppInfo.VersionText} ({AppInfo.Channel}) {where}\r\n{ex}\r\n\r\n");
            }
        }
        catch { /* registrar o erro nunca pode causar outro erro */ }
    }

    /// <summary>Diagnóstico de abertura/atualização: só grava se EXECUTOR_TRACE=1 (em logs\trace.log).</summary>
    public static void Trace(string text)
    {
        if (Environment.GetEnvironmentVariable("EXECUTOR_TRACE") != "1") return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {text}\r\n");
        }
        catch { }
    }

    /// <summary>Liga os coletores globais: erro de qualquer thread ou tarefa esquecida vai para o arquivo.</summary>
    public static void Hook()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("fatal", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("tarefa", e.Exception); e.SetObserved(); };
    }
}

/// <summary>Uma cópia do app por canal: abrir de novo só traz a janela existente para a frente.</summary>
public static class SingleInstance
{
    private static Mutex? _mutex;

    /// <summary>true = esta é a única instância (pode continuar). false = já havia outra; ela foi trazida para a frente.</summary>
    public static bool TryAcquire(bool waitForPrevious = false)
    {
        // modos de teste (captura de tela, autoteste) rodam ao lado do app aberto
        if (Environment.GetEnvironmentVariable("EXECUTOR_SHOT") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("EXECUTOR_SELFTEST") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("EXECUTOR_EDITTEST") == "1") return true;
        try
        {
            // aberto por uma atualização: o processo antigo ainda pode estar saindo, então espera até 20 s em vez de desistir
            var m = new Mutex(false, $@"Local\Lazuli-{AppInfo.Channel}-SingleInstance");
            bool owned;
            try { owned = m.WaitOne(waitForPrevious ? 20000 : 0); }
            catch (AbandonedMutexException) { owned = true; } // o dono anterior caiu sem soltar: o mutex agora é nosso
            if (owned) { _mutex = m; return true; }
            m.Dispose();
            BringOtherToFront();
            return false;
        }
        catch { return true; } // sem mutex, melhor abrir do que travar o app
    }

    private static void BringOtherToFront()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                using (p)
                {
                    if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(p.MainWindowHandle, 9 /* SW_RESTORE */);
                    SetForegroundWindow(p.MainWindowHandle);
                }
            }
        }
        catch { /* só um favor ao usuário */ }
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
}
