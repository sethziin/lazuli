using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Executor.UI.Services;

/// <summary>Um tema pronto: cor de fundo e cor de destaque. Todo o resto da paleta é calculado a partir delas.</summary>
public sealed record ThemePreset(string Name, string Background, string Accent);

/// <summary>
/// Temas do app (valem nos dois layouts). Os pincéis do Theme.axaml são instâncias únicas compartilhadas por todo o app,
/// então trocar a cor deles muda a interface inteira na hora, sem reiniciar.
/// Legibilidade: o fundo é sempre mantido escuro (cor muito clara é escurecida), o destaque ganha contraste mínimo com o
/// fundo, e com imagem de fundo as superfícies ficam entre 70% e 97% opacas por cima dela.
/// </summary>
public static class ThemeManager
{
    public static readonly IReadOnlyList<ThemePreset> Presets =
    [
        new("Graphite", "#131315", "#E4E4E7"),
        new("Lazuli", "#0F1424", "#6C8CFF"),
        new("Ocean", "#0B171B", "#4FD1C5"),
        new("Forest", "#0E1710", "#6FCF97"),
        new("Plum", "#17101D", "#C792EA"),
        new("Ember", "#1A1311", "#FF8A5B"),
        new("Rose", "#1B1115", "#F28CA6"),
    ];

    public const string Custom = "Custom";
    public const double MinDim = 0.70, MaxDim = 0.97;

    /// <summary>Imagem de fundo carregada (reduzida), ou null.</summary>
    public static Bitmap? Background { get; private set; }

    /// <summary>Disparado (na thread da interface) quando a imagem ou o desfoque mudam.</summary>
    public static event Action? BackgroundChanged;

    private static AppSettings? _settings;
    private static string? _loadedImage;
    private static int _imageRequest;
    private static string? _failedImage;

    public static void Attach(AppSettings settings)
    {
        if (_settings is not null) _settings.PropertyChanged -= OnSettingsChanged;
        _settings = settings;
        settings.PropertyChanged += OnSettingsChanged;
        Apply(settings);
    }

    private static void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ThemeName) or nameof(AppSettings.CustomBackground) or nameof(AppSettings.CustomAccent)
            or nameof(AppSettings.BackgroundImage) or nameof(AppSettings.ImageDim) or nameof(AppSettings.ImageBlur))
            Apply((AppSettings)sender!);
    }

    // ---------- paleta ----------

    public static (Color Bg, Color Accent) Resolve(AppSettings s)
    {
        var preset = Presets.FirstOrDefault(p => p.Name == s.ThemeName);
        var bgHex = preset?.Background ?? s.CustomBackground;
        var acHex = preset?.Accent ?? s.CustomAccent;
        var bg = TryParse(bgHex) ?? Color.Parse("#131315");
        var ac = TryParse(acHex) ?? Color.Parse("#E4E4E7");
        return (EnsureDark(bg), EnsureContrast(ac, bg));
    }

    public static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var t = hex.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        return t.Length is 4 or 7 && Color.TryParse(t, out var c) ? Color.FromRgb(c.R, c.G, c.B) : null;
    }

    private static double Lum(Color c)
    {
        static double L(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    private static Color Mix(Color a, Color b, double t, byte alpha = 255) => Color.FromArgb(alpha,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Texto claro precisa de fundo escuro: cores claras demais são escurecidas até ficarem legíveis.</summary>
    private static Color EnsureDark(Color c)
    {
        for (var i = 0; i < 40 && Lum(c) > 0.045; i++) c = Mix(c, Colors.Black, 0.12);
        return c;
    }

    /// <summary>O destaque (botão principal) precisa se separar do fundo: contraste mínimo 3:1.</summary>
    private static Color EnsureContrast(Color accent, Color bg)
    {
        for (var i = 0; i < 40 && (Lum(accent) + 0.05) / (Lum(bg) + 0.05) < 3.0; i++) accent = Mix(accent, Colors.White, 0.12);
        return accent;
    }

    public static void Apply(AppSettings s)
    {
        if (Application.Current is null) return;
        var (bg, accent) = Resolve(s);
        var white = Colors.White;
        // só trata como "com imagem" se o arquivo existe e não falhou ao abrir (senão as superfícies ficariam translúcidas sobre nada)
        var useImage = s.HasBackgroundImage && s.BackgroundImage != _failedImage && File.Exists(ImagePath(s.BackgroundImage!));
        var dim = double.IsFinite(s.ImageDim) ? Math.Clamp(s.ImageDim, MinDim, MaxDim) : 0.85;
        byte A(double v) => (byte)Math.Round(255 * v);

        var chrome = Mix(bg, white, 0.0);
        var editor = Mix(bg, Colors.Black, 0.26);
        var hover = Mix(bg, white, 0.04);

        Set("Chrome", Mix(bg, white, 0, useImage ? A(dim) : (byte)255));
        Set("Panel", Mix(bg, white, 0, useImage ? A(dim) : (byte)255));
        Set("Editor", Mix(editor, editor, 0, useImage ? A(Math.Min(1, dim + 0.04)) : (byte)255));
        Set("ChromeSolid", chrome);
        Set("Lift", useImage ? Color.FromArgb(10, 255, 255, 255) : bg);
        Set("EditorSolid", editor);
        Set("Hover", hover);
        Set("Clear", Mix(hover, hover, 0, 0)); // transparente, mas com o RGB do Hover: sem "flash" cinza nas transições
        Set("Active", Mix(bg, white, 0.08));
        Set("Line", Mix(bg, white, 0.068));
        Set("LineStrong", Mix(bg, white, 0.123));
        Set("Text", Mix(bg, white, 0.89));
        Set("Muted", Mix(bg, white, 0.55));
        Set("Faint", Mix(bg, white, 0.34));
        Set("Accent", accent);
        Set("AccentHover", Mix(accent, white, 0.5));
        Set("AccentPress", Mix(accent, Colors.Black, 0.12));
        Set("AccentLine", Mix(bg, accent, 0.4));
        Set("OnAccent", Lum(accent) > 0.35 ? Color.FromRgb(0x0E, 0x0E, 0x10) : Color.FromRgb(0xF5, 0xF5, 0xF7));

        UpdateBackgroundImage(s);
    }

    private static void Set(string key, Color color)
    {
        if (Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) && v is SolidColorBrush b)
            b.Color = color;
    }

    // ---------- imagem de fundo ----------

    public static string ImagePath(string fileName) => Path.Combine(Storage.LocalRoot, fileName);

    private static void UpdateBackgroundImage(AppSettings s)
    {
        var name = s.BackgroundImage;
        if (string.IsNullOrEmpty(name) || name == _failedImage)
        {
            _loadedImage = null;
            var had = Background is not null;
            Background = null;
            _imageRequest++;
            BackgroundChanged?.Invoke();
            if (had) { }
            return;
        }
        if (name == _loadedImage) { BackgroundChanged?.Invoke(); return; } // só o desfoque mudou
        _loadedImage = name;
        var request = ++_imageRequest;
        Task.Run(() =>
        {
            try
            {
                var path = ImagePath(name);
                if (!File.Exists(path)) return null;
                using var fs = File.OpenRead(path);
                return Bitmap.DecodeToWidth(fs, 1920); // reduz: uma foto de 8K não precisa ficar inteira na memória
            }
            catch { return null; }
        }).ContinueWith(t =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (request != _imageRequest) return;
                Background = t.Result;
                if (t.Result is null && _settings is not null) { _failedImage = name; Apply(_settings); return; } // imagem sumiu ou está corrompida
                BackgroundChanged?.Invoke();
            });
        });
    }

    /// <summary>Valida e copia a imagem escolhida para a pasta de dados (o original pode ser movido depois). Devolve o nome do arquivo.</summary>
    public static string? ImportImage(string sourcePath, out string? error)
    {
        error = null;
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")) { error = "Use a PNG, JPG, BMP or WebP image."; return null; }
            var info = new FileInfo(sourcePath);
            if (!info.Exists) { error = "That file doesn't exist anymore."; return null; }
            if (info.Length > 25L * 1024 * 1024) { error = "That image is too big (max 25 MB)."; return null; }
            using (var test = File.OpenRead(sourcePath)) using (Bitmap.DecodeToWidth(test, 64)) { } // não é imagem de verdade → exceção

            Directory.CreateDirectory(Storage.LocalRoot);
            var name = $"background-{DateTime.UtcNow.Ticks}{ext}";
            File.Copy(sourcePath, ImagePath(name), true);
            return name;
        }
        catch { error = "Couldn't open that image."; return null; }
    }

    /// <summary>Apaga as imagens de fundo que não são mais a atual.</summary>
    public static void CleanOldImages(string? keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Storage.LocalRoot, "background-*"))
                if (!string.Equals(Path.GetFileName(f), keep, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
