using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Executor.UI.Services;

/// <summary>Preferências do usuário. Salvas em %APPDATA%\Executor\settings.json a cada mudança.</summary>
public partial class AppSettings : ObservableObject
{
    [ObservableProperty] private double _editorFontSize = 13;
    [ObservableProperty] private string _syntaxTheme = "DarkPlus";
    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private bool _showLineNumbers = true;
    [ObservableProperty] private bool _highlightCurrentLine = true;
    [ObservableProperty] private int _tabSize = 4;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private bool _showOutputOnStart;
    [ObservableProperty] private bool _restoreTabs = true;
    [ObservableProperty] private bool _showSplash = true;
    [ObservableProperty] private string _performanceMode = "Auto";
    [ObservableProperty] private bool _autoPairs = true;
    [ObservableProperty] private bool _smartBlocks = true;
    /// <summary>Layout simples: só abas, editor e ações (sem barra lateral, Início e barra de status).</summary>
    [ObservableProperty] private bool _simpleLayout = true;

    // ---- Aparência (veja Services\ThemeManager.cs) ----
    /// <summary>Nome de um tema pronto, ou "Custom" para usar CustomBackground/CustomAccent.</summary>
    [ObservableProperty] private string _themeName = "Graphite";
    [ObservableProperty] private string _customBackground = "#131315";
    [ObservableProperty] private string _customAccent = "#6C8CFF";
    /// <summary>Nome do arquivo da imagem de fundo (copiada para %LOCALAPPDATA%\Lazuli); null = sem imagem.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBackgroundImage))] private string? _backgroundImage;
    /// <summary>Opacidade das superfícies por cima da imagem (0,70–0,97): quanto maior, mais legível e mais discreta a imagem.</summary>
    [ObservableProperty] private double _imageDim = 0.85;
    [ObservableProperty] private double _imageBlur;
    [System.Text.Json.Serialization.JsonIgnore] public bool HasBackgroundImage => !string.IsNullOrEmpty(BackgroundImage);

    /// <summary>Id do backend escolhido (ex.: "xeno", "demo"). null = automático (o único instalado, senão a demonstração). Vale na próxima abertura do app.</summary>
    [ObservableProperty] private string? _backendId;

    // Geometria da janela (0 = ainda não salva). Guardada ao fechar.
    [ObservableProperty] private double _windowWidth;
    [ObservableProperty] private double _windowHeight;
    [ObservableProperty] private int _windowX;
    [ObservableProperty] private int _windowY;
    [ObservableProperty] private bool _hasWindowPos;
    [ObservableProperty] private bool _windowMaximized;

    public void CopyWindowFrom(AppSettings o)
    {
        WindowWidth = o.WindowWidth; WindowHeight = o.WindowHeight; WindowX = o.WindowX; WindowY = o.WindowY;
        HasWindowPos = o.HasWindowPos; WindowMaximized = o.WindowMaximized;
    }

    /// <summary>Copia só as preferências (não a geometria da janela).</summary>
    public void CopyFrom(AppSettings o)
    {
        EditorFontSize = o.EditorFontSize; SyntaxTheme = o.SyntaxTheme; WordWrap = o.WordWrap;
        ShowLineNumbers = o.ShowLineNumbers; HighlightCurrentLine = o.HighlightCurrentLine; TabSize = o.TabSize;
        AlwaysOnTop = o.AlwaysOnTop; ShowOutputOnStart = o.ShowOutputOnStart; RestoreTabs = o.RestoreTabs; ShowSplash = o.ShowSplash; PerformanceMode = o.PerformanceMode; AutoPairs = o.AutoPairs; SmartBlocks = o.SmartBlocks; SimpleLayout = o.SimpleLayout;
        ThemeName = o.ThemeName; CustomBackground = o.CustomBackground; CustomAccent = o.CustomAccent;
        BackgroundImage = o.BackgroundImage; ImageDim = o.ImageDim; ImageBlur = o.ImageBlur;
    }
}

public sealed record SavedTab(string Title, string Content, bool IsMain, string? FilePath = null, string? SavedContent = null, bool Selected = false);

/// <summary>Leitura e escrita dos arquivos de configuração e sessão.</summary>
public static class Storage
{
    // Padrão do Windows: configurações pequenas em %APPDATA% (roaming); o que é grande ou específico desta máquina
    // (cache, DLL baixada do backend, logs) em %LOCALAPPDATA%. O app em si fica em %LOCALAPPDATA%\Programs\Lazuli.
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazuli");
    internal static string LocalRoot => LocalDir;
    private static readonly string LocalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lazuli");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Onde cada backend pode gravar dados próprios (%LOCALAPPDATA%\Lazuli\backends\&lt;id&gt;).</summary>
    public static string BackendDataRoot => Path.Combine(LocalDir, "backends");

    /// <summary>Cache de dados baixados (nomes e ícones de jogos): %LOCALAPPDATA%\Lazuli\cache. Pode ser apagado à vontade.</summary>
    public static string CacheDir => Path.Combine(LocalDir, "cache");

    /// <summary>Logs de erro: %LOCALAPPDATA%\Lazuli\logs.</summary>
    public static string LogDir => Path.Combine(LocalDir, "logs");

    /// <summary>Scripts do usuário: %APPDATA%\Lazuli\Scripts.</summary>
    public static string ScriptsDir => Path.Combine(Dir, "Scripts");

    static Storage() => MigrateFromExecutor();

    /// <summary>Versões antigas guardavam tudo em %APPDATA%\Executor. Na primeira abertura do Lazuli, move para os lugares novos (uma vez).</summary>
    private static void MigrateFromExecutor()
    {
        try
        {
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Executor");
            if (!Directory.Exists(old)) return;
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(LocalDir);
            // item por item: só move o que ainda não existe no destino (nunca sobrescreve nada do Lazuli)
            void Move(string name, string destDir)
            {
                var from = Path.Combine(old, name);
                var to = Path.Combine(destDir, name);
                if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
                else if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
            }
            foreach (var n in new[] { "settings.json", "session.json", "Scripts" }) Move(n, Dir);
            foreach (var n in new[] { "backends", "cache", "logs" }) Move(n, LocalDir);
            if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old);
        }
        catch { /* sem migração, o app começa com padrões: nunca impede de abrir */ }
    }

    private static string SettingsFile => Path.Combine(Dir, "settings.json");
    private static string SessionFile => Path.Combine(Dir, "session.json");

    public static AppSettings LoadSettings()
    {
        var s = new AppSettings();
        try
        {
            if (File.Exists(SettingsFile) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) is { } saved)
                { s.CopyFrom(saved); s.CopyWindowFrom(saved); s.BackendId = saved.BackendId; } // BackendId fica fora de CopyFrom: "Restaurar padrões" não troca o backend
        }
        catch { /* arquivo corrompido: usa padrões */ }
        return s;
    }

    public static void SaveSettings(AppSettings s) => TryWrite(SettingsFile, JsonSerializer.Serialize(s, Json));

    public static List<SavedTab> LoadSession()
    {
        try
        {
            if (File.Exists(SessionFile))
                return JsonSerializer.Deserialize<List<SavedTab>>(File.ReadAllText(SessionFile)) ?? [];
        }
        catch { }
        return [];
    }

    public static void SaveSession(IEnumerable<SavedTab> tabs) => TryWrite(SessionFile, JsonSerializer.Serialize(tabs, Json));

    public static void ClearSession() { try { File.Delete(SessionFile); } catch { } }

    private static void TryWrite(string file, string text)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(file, text); }
        catch { /* sem permissão de escrita: ignora */ }
    }
}
