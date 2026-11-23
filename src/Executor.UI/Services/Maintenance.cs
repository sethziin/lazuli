using System.Diagnostics;

namespace Executor.UI.Services;

/// <summary>
/// Faxina de arquivos temporários da própria máquina do usuário: o que o Executor/o backend criam em disco para
/// funcionar (cache de ícones/nomes de jogos, dados baixados do backend, a pasta de trabalho com o log da DLL) e o
/// histórico de logs do Roblox. NÃO toca nos scripts do usuário, nas configurações do app, nem no plugin instalado.
/// Cada item é apagado em try/catch: o que estiver em uso (ex.: a DLL já carregada) é pulado, não quebra o resto.
/// </summary>
public static class Maintenance
{
    private const string RobloxProcess = "RobloxPlayerBeta";

    public sealed record CleanResult(int Files, long Bytes)
    {
        public string Human => Bytes >= 1048576 ? $"{Bytes / 1048576.0:0.#} MB"
            : Bytes >= 1024 ? $"{Bytes / 1024.0:0} KB" : $"{Bytes} B";
    }

    /// <summary>Apaga os arquivos que o Executor e o backend criaram para funcionar (mantém scripts e configurações).</summary>
    public static CleanResult CleanExecutor()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        var targets = new List<string> { Storage.CacheDir, Storage.BackendDataRoot };
        if (exeDir is not null) targets.Add(Path.Combine(exeDir, "bin")); // CHRS/PLTBL/DLCTBL_FRTS/DEBUG.txt e DLLs copiadas
        return ClearAll(targets);
    }

    /// <summary>Apaga o histórico de logs do Roblox (onde ficam os jogos e servidores por onde o usuário passou).</summary>
    public static CleanResult CleanRobloxHistory()
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
        return ClearAll([logs]);
    }

    /// <summary>Fecha o Roblox, faz as duas limpezas e reabre o Roblox do zero.</summary>
    public static CleanResult StartRobloxClean()
    {
        CloseRoblox();
        var a = CleanRobloxHistory();
        var b = CleanExecutor();
        RobloxRecent.OpenRoblox();
        return new CleanResult(a.Files + b.Files, a.Bytes + b.Bytes);
    }

    public static bool RobloxRunning
    {
        get
        {
            var ps = Process.GetProcessesByName(RobloxProcess);
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }
    }

    private static void CloseRoblox()
    {
        foreach (var p in Process.GetProcessesByName(RobloxProcess))
            using (p)
                try { p.Kill(); p.WaitForExit(3000); } catch { }
    }

    /// <summary>Esvazia o conteúdo das pastas (mantém a pasta em si); conta e soma só o que conseguiu apagar.</summary>
    private static CleanResult ClearAll(IEnumerable<string> dirs)
    {
        var files = 0;
        long bytes = 0;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in SafeEnumerate(dir))
            {
                try
                {
                    var len = new FileInfo(f).Length;
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    files++; bytes += len;
                }
                catch { /* em uso ou sem permissão: pula */ }
            }
            // remove subpastas que ficaram vazias (de baixo para cima)
            foreach (var sub in SafeEnumerateDirs(dir).OrderByDescending(d => d.Length))
                try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch { }
        }
        return new CleanResult(files, bytes);
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).ToList(); }
        catch { return []; }
    }
}
