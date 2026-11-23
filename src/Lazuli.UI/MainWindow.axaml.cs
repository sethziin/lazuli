using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Search;
using Lazuli.UI.Editing;
using AvaloniaEdit.TextMate;
using Lazuli.Contracts;
using Lazuli.UI.Services;
using Lazuli.UI.ViewModels;
using TextMateSharp.Grammars;

namespace Lazuli.UI;

public static class Converters
{
    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#A1A1AA"));
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#7FD69B"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#E0B458"));
    private static readonly IBrush Err = new SolidColorBrush(Color.Parse("#F07B7B"));

    public static readonly FuncValueConverter<string?, IBrush?> LevelBrush = new(level => level switch
    {
        nameof(LogLevel.Success) => Ok,
        nameof(LogLevel.Warning) => Warn,
        nameof(LogLevel.Error) => Err,
        _ => Info,
    });
}

public partial class MainWindow : Window
{

    private static readonly FilePickerFileType LuaFiles = new("Scripts Lua") { Patterns = ["*.lua"] };
    private static readonly FilePickerFileType AllFiles = new("Todos os arquivos") { Patterns = ["*"] };

    // realce de sintaxe: criado depois do 1º frame (a janela abre ~150 ms mais rápido)
    private RegistryOptions? _registry;
    private TextMate.Installation? _textMate;
    private SearchPanel? _search;
    private LuaEditorBehavior? _lua;
    private bool _syncing;
    private MainViewModel? _vm;

    // geometria "normal" (não maximizada) para lembrar entre execuções
    private Size _normalSize;
    private PixelPoint _normalPos;
    private bool _windowRestored;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Motion.WindowOpen(this);
        ThemeManager.BackgroundChanged += UpdateBackground;
        Closed += (_, _) => ThemeManager.BackgroundChanged -= UpdateBackground;
        Opened += (_, _) => UpdateBackground();
        Opened += (_, _) => UpdateTaskbarBadge();

        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.ConvertTabsToSpaces = true;
        _search = SearchPanel.Install(Editor); // Ctrl+F, F3 / Shift+F3
        // edição de Lua: fecha ( [ { e aspas, indenta blocos, escreve o end, Ctrl+/ comenta
        _lua = new LuaEditorBehavior(Editor, () => new string(' ', _vm?.Settings.TabSize ?? 4),
            () => _vm?.Settings.AutoPairs ?? true, () => _vm?.Settings.SmartBlocks ?? true);

        var area = Editor.TextArea;
        area.Caret.PositionChanged += (_, _) => CaretText.Text = $"Ln {area.Caret.Line}, Col {area.Caret.Column}";
        Editor.TextChanged += (_, _) =>
        {
            if (!_syncing && _vm?.SelectedTab is { } tab) tab.Content = Editor.Text;
        };
        // Ctrl + roda do mouse = zoom
        Editor.AddHandler(PointerWheelChangedEvent, OnEditorWheel, RoutingStrategies.Tunnel);

        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxBtn.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseBtn.Click += (_, _) => Close();

        // o "+" fica colado na última aba; a lista só rola quando não cabe mais
        TabsHost.SizeChanged += (_, _) => UpdateTabsMaxWidth();
        NewTabBtn.SizeChanged += (_, _) => UpdateTabsMaxWidth();
        TabsList.PointerWheelChanged += OnTabsWheel;

        // abas: botão direito = menu, botão do meio = fechar, duplo clique / F2 = renomear
        TabsList.AddHandler(PointerPressedEvent, OnTabsPointer, RoutingStrategies.Tunnel);
        TabsList.DoubleTapped += OnTabsDoubleTapped;
        TabsList.AddHandler(KeyDownEvent, OnTabsKey, RoutingStrategies.Tunnel);
        TabsList.AddHandler(LostFocusEvent, OnTabsLostFocus);

        LibraryList.SelectionChanged += (_, _) =>
        {
            if (LibraryList.SelectedItem is ScriptEntry entry) _vm?.OpenScriptCommand.Execute(entry);
            LibraryList.SelectedItem = null;
        };

        // paleta de comandos e camadas
        PaletteBtn.Click += (_, _) => _vm?.OpenPalette();
        PaletteBox.AddHandler(KeyDownEvent, OnPaletteKey, RoutingStrategies.Tunnel);
        PaletteScrim.PointerPressed += (_, _) => { _vm?.ClosePalette(); FocusEditor(); };
        TabMenuScrim.PointerPressed += (_, _) => _vm?.CloseTabMenuCommand.Execute(null);
        PaletteList.Tapped += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true)?.DataContext is PaletteCommand cmd)
                _vm?.RunPaletteItem(cmd);
        };

        // arrastar e soltar arquivos
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // atalhos em túnel: funcionam mesmo com o foco dentro do editor
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);

        // geometria da janela
        Resized += (_, e) => { if (WindowState == WindowState.Normal) _normalSize = e.ClientSize; };
        PositionChanged += (_, _) => { if (WindowState == WindowState.Normal) _normalPos = Position; };
        Opened += (_, _) =>
        {
            if (WindowState == WindowState.Normal) { _normalSize = new Size(Width, Height); _normalPos = Position; }
            // só depois que a janela já apareceu: carrega o realce de sintaxe (é o que mais pesa na abertura)
            Dispatcher.UIThread.Post(InitHighlighting, DispatcherPriority.Background);
        };
        Closing += (_, _) =>
        {
            SaveWindowState();
            _vm?.FlushSettings();
            _vm?.SaveSession();
        };

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) { _vm.PropertyChanged -= OnVmChanged; _vm.Settings.PropertyChanged -= OnSettingsChanged; }
            _vm = DataContext as MainViewModel;
            if (_vm is null) return;
            _vm.PropertyChanged += OnVmChanged;
            _vm.Settings.PropertyChanged += OnSettingsChanged;
            _vm.EditorReloadRequested += () => LoadTab(_vm.SelectedTab);
            _vm.SettingsRequested += OpenSettings;
            _vm.OpenFileRequested += () => _ = OpenFilesAsync();
            _vm.FindRequested += OpenSearch;
            _vm.ToggleCommentRequested += () => { Editor.Focus(); _lua?.ToggleComment(); };
            _vm.RenameStarted += OnRenameStarted;
            _vm.PaletteOpened += OnPaletteOpened;
            _vm.CopyConsoleRequested += () => _ = CopyConsoleAsync();
            _vm.SaveHandler = SaveTabAsync;
            _vm.Console.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => ConsoleScroll.ScrollToEnd());
            Perf.Apply(this, _vm.Settings);
            RestoreWindowState();
            ApplyAllSettings();
            LoadTab(_vm.SelectedTab);
        };
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow { DataContext = _vm, Topmost = Topmost };
        Perf.Apply(_settingsWindow, _vm!.Settings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.ShowDialog(this);
    }

    private void FocusEditor() => Dispatcher.UIThread.Post(() => Editor.Focus(), DispatcherPriority.Background);

    /// <summary>Abre o painel de busca do editor (Ctrl+F).</summary>
    public void OpenSearch()
    {
        _search?.Open();
        Editor.Focus();
    }

    // ---------- Atalhos ----------

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Esc fecha a camada aberta (paleta, confirmação ou menu)
        if (e.Key == Key.Escape)
        {
            if (_vm.PaletteOpen) { _vm.ClosePalette(); FocusEditor(); }
            else if (_vm.ConfirmOpen) _vm.ConfirmCancelCommand.Execute(null);
            else if (_vm.TabMenuOpen) _vm.CloseTabMenuCommand.Execute(null);
            else return;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2 && !ctrl) { _vm.BeginRenameCommand.Execute(null); e.Handled = true; return; }
        if (!ctrl || _vm.ConfirmOpen) return;

        switch (e.Key)
        {
            case Key.Enter: _vm.RunCommand.Execute(null); break;
            case Key.T:
            case Key.N: _vm.NewTabCommand.Execute(null); break;
            case Key.W: _vm.CloseTabCommand.Execute(null); break;
            case Key.O: _vm.OpenFileCommand.Execute(null); break;
            case Key.S: (shift ? _vm.SaveAsCommand : _vm.SaveCommand).Execute(null); break;
            case Key.B: _vm.ToggleLibraryCommand.Execute(null); break;
            case Key.J: _vm.ToggleConsoleCommand.Execute(null); break;
            case Key.K: if (_vm.PaletteOpen) { _vm.ClosePalette(); FocusEditor(); } else _vm.OpenPalette(); break;
            case Key.P when shift: _vm.OpenPalette(); break;
            case Key.OemComma: _vm.OpenSettingsCommand.Execute(null); break;
            case Key.OemPlus:
            case Key.Add: _vm.Zoom(+1); break;
            case Key.OemMinus:
            case Key.Subtract: _vm.Zoom(-1); break;
            case Key.D0:
            case Key.NumPad0: _vm.ZoomReset(); break;
            case Key.Tab: _vm.CycleTab(shift ? -1 : 1); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnEditorWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_vm is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
        _vm.Zoom(e.Delta.Y > 0 ? +1 : -1);
        e.Handled = true;
    }

    // ---------- Paleta de comandos ----------

    private void OnPaletteOpened()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Motion.FadeIn(PaletteLayer, 110);
            PaletteBox.Focus();
            PaletteBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void OnPaletteKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Down: _vm.MovePalette(+1); break;
            case Key.Up: _vm.MovePalette(-1); break;
            case Key.Enter: _vm.RunPaletteItem(); FocusEditor(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------- Arquivos ----------

    private async Task<IStorageFolder?> StartFolderAsync()
    {
        try
        {
            if (_vm?.LibraryFolder is { } d && Directory.Exists(d)) return await StorageProvider.TryGetFolderFromPathAsync(d);
        }
        catch { /* sem pasta inicial: o sistema escolhe */ }
        return null;
    }

    private async Task OpenFilesAsync()
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open script",
            AllowMultiple = true,
            FileTypeFilter = [LuaFiles, AllFiles],
            SuggestedStartLocation = await StartFolderAsync(),
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path) _vm.OpenFromPath(path);
    }

    private static string SafeFileName(string title)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(title.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "script" : s;
    }

    /// <summary>Salva direto se a aba já tem arquivo; senão (ou com forceDialog) pergunta onde.</summary>
    private async Task<bool> SaveTabAsync(ScriptTab tab, bool forceDialog)
    {
        if (_vm is null) return false;
        if (!forceDialog && tab.FilePath is { } existing) return _vm.WriteTab(tab, existing);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save script",
            SuggestedFileName = tab.FilePath is { } p ? System.IO.Path.GetFileName(p) : SafeFileName(tab.Title) + ".lua",
            DefaultExtension = "lua",
            FileTypeChoices = [LuaFiles, AllFiles],
            SuggestedStartLocation = await StartFolderAsync(),
        });
        return file?.TryGetLocalPath() is { } path && _vm.WriteTab(tab, path);
    }

    private async Task CopyConsoleAsync()
    {
        if (_vm is null || Clipboard is not { } cb) return;
        await cb.SetTextAsync(_vm.ConsoleText);
        _vm.ShowToast("Output copiado");
    }

    // ---------- Arrastar e soltar ----------

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (_vm is null || !HasFiles(e)) return;
        _vm.DropHint = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_vm is not null) _vm.DropHint = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is null) return;
        _vm.DropHint = false;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path && File.Exists(path)) _vm.OpenFromPath(path);
        e.Handled = true;
    }

    // ---------- Geometria da janela ----------

    /// <summary>Mostra (ou esconde) a imagem de fundo e aplica o desfoque escolhido.</summary>
    private void UpdateBackground()
    {
        var img = ThemeManager.Background;
        BgImage.Source = img;
        BgImage.IsVisible = img is not null;
        var blur = Math.Clamp(double.IsFinite(_vm?.Settings.ImageBlur ?? 0) ? _vm?.Settings.ImageBlur ?? 0 : 0, 0, 16);
        BgImage.Effect = blur > 0.5 ? new Avalonia.Media.BlurEffect { Radius = blur } : null;
    }

    private void UpdateTaskbarBadge()
    {
        if (_vm is null || TryGetPlatformHandle()?.Handle is not { } h) return;
        TaskbarBadge.Set(h, _vm.StatusDot, _vm.ConnectionText);
    }

    private void RestoreWindowState()
    {
        if (_vm is null || _windowRestored) return;
        _windowRestored = true;
        var s = _vm.Settings;
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight) { Width = s.WindowWidth; Height = s.WindowHeight; }
        // layout detalhado: janela maior (mais páginas e painéis)
        // layout simples: janela compacta
        if (_vm.IsSimple) { MinWidth = 560; MinHeight = 340; Width = 760; Height = 460; }
        if (!_vm.IsSimple) { MinWidth = 960; MinHeight = 600; if (Width < 1120) Width = 1120; if (Height < 700) Height = 700; }
        // sempre abre centralizada (só o tamanho é lembrado)
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowState()
    {
        if (_vm is null || !_vm.PersistenceEnabled) return;
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (_normalSize.Width > 0)
        {
            s.WindowWidth = _normalSize.Width;
            s.WindowHeight = _normalSize.Height;
            s.WindowX = _normalPos.X;
            s.WindowY = _normalPos.Y;
            s.HasWindowPos = true;
        }
    }

    // ---------- Configurações aplicadas ao editor ----------

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.SyntaxTheme): ApplyTheme(); break;
            case nameof(AppSettings.PerformanceMode):
                Perf.Apply(this, _vm!.Settings);
                if (_settingsWindow is not null) Perf.Apply(_settingsWindow, _vm.Settings);
                break;
            case nameof(AppSettings.HighlightCurrentLine):
            case nameof(AppSettings.TabSize): ApplyOptions(); break;
            case nameof(AppSettings.ShowLineNumbers): Dispatcher.UIThread.Post(CleanMargins); break;
        }
    }

    private void ApplyAllSettings()
    {
        ApplyTheme();
        ApplyOptions();
        // o editor cria a margem de números depois do binding; limpa depois disso
        Dispatcher.UIThread.Post(CleanMargins, DispatcherPriority.Loaded);
    }

    private ThemeName SyntaxThemeName() =>
        Enum.TryParse<ThemeName>(_vm?.Settings.SyntaxTheme, out var t) ? t : ThemeName.DarkPlus;

    /// <summary>Instala o realce de sintaxe (Lua). Roda uma vez, depois do 1º frame.</summary>
    private void InitHighlighting()
    {
        if (_textMate is not null) return;
        _registry = new RegistryOptions(SyntaxThemeName());
        _textMate = Editor.InstallTextMate(_registry);
        _textMate.SetGrammar(_registry.GetScopeByLanguageId(_registry.GetLanguageByExtension(".lua").Id));
        ApplyTheme(); // reaplica as cores do app por cima do tema do TextMate
    }

    private void ApplyTheme()
    {
        if (_textMate is not null && _registry is not null) _textMate.SetTheme(_registry.LoadTheme(SyntaxThemeName()));
        // o tema TextMate troca cores do editor; reaplica as nossas por cima
        // pincéis do tema (instâncias compartilhadas): mudar o tema muda o editor na hora
        Editor.Background = Brushes.Transparent; // quem pinta o fundo é o painel atrás (uma camada só: veja ThemeManager)
        var area = Editor.TextArea;
        area.SelectionBrush = (IBrush)this.FindResource("LineStrong")!;
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 2;
        area.TextView.CurrentLineBackground = (IBrush)this.FindResource("Hover")!;
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        area.Caret.CaretBrush = (IBrush)this.FindResource("Text")!;
    }

    private void ApplyOptions()
    {
        if (_vm is null) return;
        Editor.Options.HighlightCurrentLine = _vm.Settings.HighlightCurrentLine;
        Editor.Options.IndentationSize = _vm.Settings.TabSize;
    }

    /// <summary>Remove a linha vertical entre números e código e dá respiro às margens.</summary>
    private void CleanMargins()
    {
        var margins = Editor.TextArea.LeftMargins;
        foreach (var line in margins.OfType<Line>().ToList()) margins.Remove(line);
        foreach (var spacer in margins.OfType<Border>().Where(b => b.Tag as string == "spacer").ToList()) margins.Remove(spacer);
        if (margins.FirstOrDefault() is Control numbers) numbers.Margin = new Avalonia.Thickness(14, 0, 0, 0);
        margins.Add(new Border { Width = 16, Tag = "spacer" });
    }

    // ---------- Abas ----------

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        var vm = _vm!;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.StatusDot):
                UpdateTaskbarBadge();
                break;
            case nameof(MainViewModel.LibraryOpen) when vm.LibraryOpen:
                Dispatcher.UIThread.Post(() => Motion.FadeIn(LibraryPanel, 140), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.ClientsOpen) when vm.ClientsOpen:
                Dispatcher.UIThread.Post(() => Motion.FadeIn(ClientsPanel, 140), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.ConsoleOpen) when vm.ConsoleOpen:
                Dispatcher.UIThread.Post(() => Motion.FadeIn(ConsolePanel, 140), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.ConfirmOpen) when vm.ConfirmOpen:
                Dispatcher.UIThread.Post(() => Motion.FadeIn(ConfirmLayer, 110), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.TabMenuOpen) when vm.TabMenuOpen:
                Dispatcher.UIThread.Post(() => Motion.FadeIn(TabMenuLayer, 90), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.SelectedPaletteItem) when vm.SelectedPaletteItem is { } item:
                PaletteList.ScrollIntoView(item);
                break;
            case nameof(MainViewModel.SelectedTab):
                var tab = vm.SelectedTab;
                LoadTab(tab);
                // depois do layout, garante que a aba ativa (ex.: recém-criada) esteja visível
                if (tab is not null) Dispatcher.UIThread.Post(() => TabsList.ScrollIntoView(tab), DispatcherPriority.Loaded);
                break;
        }
    }

    private static ScriptTab? TabOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(true)?.DataContext as ScriptTab;

    private void OnTabsPointer(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || TabOf(e.Source) is not { } tab || e.Source is TextBox) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed)
        {
            _vm.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
        else if (props.IsRightButtonPressed)
        {
            _vm.SelectedTab = tab;
            var pos = e.GetPosition(RootGrid);
            _vm.OpenTabMenu(tab, Math.Clamp(pos.X, 6, Math.Max(6, RootGrid.Bounds.Width - 220)), 46);
            e.Handled = true;
        }
    }

    private void OnTabsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || e.Source is TextBox || e.Source is Button) return;
        if (TabOf(e.Source) is { } tab) _vm.BeginRenameCommand.Execute(tab);
    }

    private void OnTabsKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Source is not TextBox { DataContext: ScriptTab tab }) return;
        if (e.Key == Key.Enter) { _vm.CommitRename(tab); FocusEditor(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.CancelRename(tab); FocusEditor(); e.Handled = true; }
    }

    private void OnTabsLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: ScriptTab tab }) _vm?.CommitRename(tab);
    }

    private void OnRenameStarted(ScriptTab tab)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = TabsList.ContainerFromItem(tab)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (box is null) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateTabsMaxWidth()
    {
        var plus = NewTabBtn.Bounds.Width + NewTabBtn.Margin.Left + NewTabBtn.Margin.Right + TabsHost.Padding.Left;
        TabsList.MaxWidth = Math.Max(0, TabsHost.Bounds.Width - plus);
    }

    /// <summary>Roda do mouse sobre as abas rola na horizontal.</summary>
    private void OnTabsWheel(object? sender, PointerWheelEventArgs e)
    {
        var sv = TabsList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv is null) return;
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        var max = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        sv.Offset = new Vector(Math.Clamp(sv.Offset.X - delta * 60, 0, max), sv.Offset.Y);
        e.Handled = true;
    }

    private void LoadTab(ScriptTab? tab)
    {
        _syncing = true;
        Editor.Text = tab?.Content ?? "";
        _syncing = false;
    }
}
