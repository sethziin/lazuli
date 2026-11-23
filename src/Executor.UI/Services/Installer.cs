using System.Diagnostics;
using System.Reflection;
using Executor.UI.Updates;

namespace Executor.UI.Services;

/// <summary>
/// Instalação por usuário, sem instalador: o Lazuli.exe de lançamento carrega o plugin do backend dentro de si
/// (recursos "plugin/...") e, ao ser aberto de qualquer outra pasta (Downloads, Área de Trabalho…), copia-se para
/// %LOCALAPPDATA%\Programs\Lazuli, extrai o plugin em backends\ e abre a cópia instalada. É o mesmo padrão de apps como
/// o VS Code e o Discord: em %LOCALAPPDATA% a pasta é gravável sem administrador, e é isso que permite a atualização automática.
/// Builds de desenvolvimento (sem o plugin embutido) nunca se mudam de lugar.
/// </summary>
internal static class Installer
{
    private const string ResourcePrefix = "plugin/";

    /// <summary>Pasta de instalação. LAZULI_INSTALL_DIR troca (testes).</summary>
    public static string InstallDir =>
        Environment.GetEnvironmentVariable("LAZULI_INSTALL_DIR") is { Length: > 0 } e
            ? e
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Lazuli");

    private static string[] PluginResources =>
        typeof(Installer).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Se este exe não está na pasta de instalação, instala e abre a cópia instalada (true = este processo deve sair).
    /// Qualquer falha devolve false: o app abre normalmente de onde está (modo portátil), sem perder nada.
    /// LAZULI_PORTABLE=1 desliga a instalação.
    /// </summary>
    public static bool TryRelocate(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("LAZULI_PORTABLE") == "1") return false;
            var resources = PluginResources;
            if (resources.Length == 0) return false; // build de desenvolvimento
            var self = Environment.ProcessPath;
            if (self is null) return false;

            var installDir = Path.GetFullPath(InstallDir).TrimEnd('\\');
            if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(self)!).TrimEnd('\\'), installDir, StringComparison.OrdinalIgnoreCase))
                return false; // já é a cópia instalada

            var target = Path.Combine(installDir, "Lazuli.exe");
            Directory.CreateDirectory(installDir);

            var firstInstall = !File.Exists(target);
            var installed = firstInstall ? null : ReadVersion(target);
            // instala/atualiza só se não houver cópia, ou se esta for mais nova (a instalada pode ter se atualizado sozinha para algo mais novo)
            if (!IsRunning(target) && (installed is null || installed < AppInfo.Version))
            {
                var tmp = target + ".new";
                File.Copy(self, tmp, overwrite: true);
                File.Move(tmp, target, overwrite: true);
                ExtractPlugin(installDir, resources);
                if (firstInstall) CreateStartMenuShortcut(target);
            }

            var psi = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = installDir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch { return false; }
    }

    private static void ExtractPlugin(string installDir, string[] resources)
    {
        var dir = Path.Combine(installDir, "backends", "solara");
        Directory.CreateDirectory(dir);
        foreach (var name in resources)
        {
            using var s = typeof(Installer).Assembly.GetManifestResourceStream(name)!;
            var file = Path.Combine(dir, name[ResourcePrefix.Length..].Replace("solara/", "", StringComparison.Ordinal));
            using var fs = File.Create(file);
            s.CopyTo(fs);
        }
    }

    private static Version? ReadVersion(string exe)
    {
        try
        {
            var text = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+', '-')[0];
            return Version.TryParse(text, out var v) ? v : null;
        }
        catch { return null; }
    }

    private static bool IsRunning(string exe)
    {
        foreach (var p in Process.GetProcessesByName("Lazuli"))
            using (p)
                try { if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
        return false;
    }

    /// <summary>Atalho no Menu Iniciar (só na primeira instalação e só na instalação padrão, não nos testes).</summary>
    private static void CreateStartMenuShortcut(string target)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("LAZULI_INSTALL_DIR") is { Length: > 0 }) return;
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lazuli.lnk");
            if (File.Exists(lnk)) return;
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(lnk);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "Lazuli";
            link.Save();
        }
        catch { /* o atalho é só conveniência */ }
    }
}
