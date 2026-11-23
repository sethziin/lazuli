using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Executor.Contracts;
using Executor.UI.Services;

namespace Executor.UI.ViewModels;

public partial class ScriptTab : ObservableObject
{
    [ObservableProperty] private string _title;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty), nameof(ShowDirty))] private string _content;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty), nameof(ShowDirty))] private string _savedContent;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDirty))] private string? _filePath;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editTitle = "";

    public bool IsMain { get; }
    public bool CanClose => !IsMain;

    /// <summary>Conteúdo diferente do último salvo.</summary>
    public bool IsDirty => Content != SavedContent;

    /// <summary>Bolinha de "não salvo": a aba Main (rascunho) só mostra se estiver ligada a um arquivo.</summary>
    public bool ShowDirty => IsDirty && (!IsMain || FilePath is not null);

    public ScriptTab(string title, string content, bool isMain = false, string? filePath = null, string? savedContent = null)
    {
        _title = title; _content = content; IsMain = isMain;
        _filePath = filePath; _savedContent = savedContent ?? content;
    }
}

public sealed record ConsoleItem(string Time, string Text, string Level);
public sealed record Option<T>(T Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Uma linha da paleta de comandos (Ctrl+K).</summary>
public sealed record PaletteCommand(string Title, string Shortcut, Action Run);

public partial class MainViewModel : ObservableObject
{
    private readonly IRunSession _session;
    private readonly IScriptLibrary _libraryProvider;
    private List<ScriptEntry> _library = new();
    private readonly List<ConsoleItem> _allLog = new();
    private List<PaletteCommand> _commands = new();

    public AppSettings Settings { get; }
    public string Version => Updates.AppInfo.VersionText;

    public ObservableCollection<ScriptTab> Tabs { get; } = new();
    public ObservableCollection<ScriptEntry> FilteredLibrary { get; } = new();
    /// <summary>Linhas do Output já filtradas pelos chips Info / Avisos / Erros.</summary>
    public ObservableCollection<ConsoleItem> Console { get; } = new();
    public ObservableCollection<PaletteCommand> PaletteItems { get; } = new();

    public IReadOnlyList<Option<string>> ThemeOptions { get; } =
    [
        new("DarkPlus", "Dark+"), new("VisualStudioDark", "Visual Studio"), new("OneDark", "One Dark"),
        new("AtomOneDark", "Atom One"), new("Dracula", "Dracula"), new("Monokai", "Monokai"),
    ];
    public IReadOnlyList<int> TabSizes { get; } = [2, 4, 8];
    public IReadOnlyList<double> FontSizes { get; } = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22];

    [ObservableProperty] private ScriptTab? _selectedTab;
    [ObservableProperty] private bool _libraryOpen;
    [ObservableProperty] private bool _consoleOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRun))] private bool _isRunning;
    [ObservableProperty] private string _searchText = "";

    // Estado puramente visual do botão Attach (sem backend).
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AttachLabel), nameof(StatusDot), nameof(CanRun), nameof(CanAttach), nameof(ConnectionText), nameof(ConnectionBrush))] private bool _isAttached;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AttachLabel), nameof(StatusDot), nameof(CanAttach), nameof(ConnectionText), nameof(ConnectionBrush))] private bool _isAttaching;
    /// <summary>O último Attach falhou (ou a conexão caiu com o Roblox aberto): mostra o status em vermelho até a próxima tentativa.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AttachLabel), nameof(StatusDot), nameof(CanAttach), nameof(ConnectionText), nameof(ConnectionBrush))] private bool _hasError;

    /// <summary>O alvo (Roblox) está aberto? Atualizado a cada ~2 s por <see cref="StartTargetWatch"/>.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AttachLabel), nameof(StatusDot), nameof(CanAttach), nameof(ConnectionText), nameof(ConnectionBrush))] private bool _isTargetRunning = true;

    /// <summary>Indicador do rodapé.</summary>
    public string ConnectionText => IsAttaching ? "Attaching…" : IsAttached ? "Attached" : !IsTargetRunning ? "Roblox closed" : HasError ? "Error" : "Ready";
    // cinza = Roblox fechado · amarelo = aberto, sem Attach · azul = anexando · verde = anexado · vermelho = erro
    public string ConnectionBrush => IsAttaching ? "#7FB6F5" : IsAttached ? "#86E6A0" : !IsTargetRunning ? "#8E8E97" : HasError ? "#E8837B" : "#E8C46B";

    /// <summary>Executar só habilita depois do Attach e fora de uma execução.</summary>
    public bool CanRun => IsAttached && !IsRunning;

    /// <summary>Attach só habilita com o Roblox aberto (ou para soltar um Attach já feito).</summary>
    public bool CanAttach => !IsAttaching && (IsAttached || IsTargetRunning);

    public string AttachLabel => IsAttaching ? "Attaching…" : IsAttached ? "Attached" : !IsTargetRunning ? "Roblox closed" : "Attach";
    // vermelho = Roblox fechado · âmbar = Roblox aberto, sem Attach · azul = anexando · verde = anexado
    public string StatusDot => IsAttaching ? "#4C9BE8" : IsAttached ? "#3FB950" : !IsTargetRunning ? "#6B6B73" : HasError ? "#E5534B" : "#E0B458";

    private Avalonia.Threading.DispatcherTimer? _targetTimer;
    private int _watching;

    /// <summary>Começa a vigiar o alvo: habilita/desabilita o Attach e desfaz o Attach se o Roblox fechar.</summary>
    public void StartTargetWatch()
    {
        if (_targetTimer is not null) return;
        _ = PollTarget();
        _targetTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _targetTimer.Tick += async (_, _) => await PollTarget();
        _targetTimer.Start();
    }

    private async Task PollTarget()
    {
        if (System.Threading.Interlocked.Exchange(ref _watching, 1) == 1) return;
        try
        {
            if (IsAttaching || IsRunning) return; // o backend está ocupado: não mexe agora
            var (running, attached) = await Task.Run(() => (_session.IsTargetRunning, IsAttached && _session.IsAttached));
            IsTargetRunning = running;
            if (!running) HasError = false;
            if (IsAttached && !attached)
            {
                IsAttached = false;
                HasError = running;
                Log(LogLevel.Warning, running ? "Connection lost." : "Roblox closed: detached.");
                OnPropertyChanged(nameof(Status));
            }
            await RefreshClients();
        }
        catch { /* próxima volta tenta de novo */ }
        finally { System.Threading.Volatile.Write(ref _watching, 0); }
    }

    public string Status => _session.StatusText;

    /// <summary>Pasta da biblioteca, quando ela é uma pasta de verdade.</summary>
    public string? LibraryFolder => (_libraryProvider as FolderScriptLibrary)?.Folder;
    public bool HasLibraryFolder => LibraryFolder is not null;

    // ---------- Eventos para a janela ----------

    /// <summary>Disparado quando o editor precisa recarregar o texto da aba atual.</summary>
    public event Action? EditorReloadRequested;
    /// <summary>A janela principal abre o diálogo de configurações ao receber este evento.</summary>
    public event Action? SettingsRequested;
    public event Action? OpenFileRequested;
    public event Action? FindRequested;
    public event Action? ToggleCommentRequested;
    /// <summary>A aba entrou em modo de renomear: a janela dá foco à caixa de texto.</summary>
    public event Action<ScriptTab>? RenameStarted;
    public event Action? PaletteOpened;

    /// <summary>Quem sabe mostrar o diálogo de salvar: (aba, forçarSalvarComo) → salvou?</summary>
    public Func<ScriptTab, bool, Task<bool>>? SaveHandler { get; set; }

    /// <summary>O backend em uso. Define o que a UI oferece (veja <see cref="BackendCapabilities"/>). Sem informar, vale a demonstração.</summary>
    public BackendInfo Backend { get; init; } = BackendInfo.Demo;

    public bool CanListClients => Backend.Has(BackendCapabilities.ListClients);
    public bool CanKill => Backend.Has(BackendCapabilities.KillTarget);

    public MainViewModel(IScriptLibrary library, IRunSession session, AppSettings settings)
    {
        _session = session;
        _libraryProvider = library;
        _session.LogReceived += l => Avalonia.Threading.Dispatcher.UIThread.Post(() => Log(l.Level, l.Text, l.Time));
        Settings = settings;
        // grava as configurações agrupando mudanças seguidas (menos escrita em disco, bom em HD lento)
        Settings.PropertyChanged += (_, _) => { if (PersistenceEnabled) ScheduleSettingsSave(); };
        BuildCommands(); // a lista de scripts só é lida quando a barra Scripts abre (EnsureLibrary)

        // layout simples: sem Início e sem painéis laterais (só abas, editor e ações)
        IsSimple = Settings.SimpleLayout; // fixo até reiniciar
        ApplyLayoutOverride();
        ApplyLayout();

        ConsoleOpen = Settings.ShowOutputOnStart;
        RestoreSession();
        Log(LogLevel.Info, "Ready.");
    }

    // ---------- Sessão ----------

    private void RestoreSession()
    {
        var saved = Settings.RestoreTabs ? Storage.LoadSession() : [];
        var main = saved.FirstOrDefault(t => t.IsMain);
        Tabs.Add(new ScriptTab(string.IsNullOrWhiteSpace(main?.Title) ? "Main" : main!.Title, main?.Content ?? "", isMain: true, main?.FilePath, main?.SavedContent));
        foreach (var t in saved.Where(t => !t.IsMain)) Tabs.Add(new ScriptTab(t.Title, t.Content, false, t.FilePath, t.SavedContent));
        var sel = saved.Where(t => !t.IsMain).ToList().FindIndex(t => t.Selected);
        SelectedTab = sel >= 0 ? Tabs[sel + 1] : Tabs[0];
        // salva sozinho a cada 3 s se algo mudou: abas, nomes e conteúdo sobrevivem até a um fechamento forçado
        _autosave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autosave.Tick += (_, _) => SaveSessionIfChanged();
        _autosave.Start();
    }

    private Avalonia.Threading.DispatcherTimer? _autosave;
    private string? _lastSessionJson;

    private void SaveSessionIfChanged()
    {
        if (!PersistenceEnabled || !Settings.RestoreTabs) return;
        var snap = string.Join("\u0001", Tabs.Select(t => $"{t.Title}\u0002{t.Content}\u0002{t.FilePath}\u0002{t == SelectedTab}"));
        if (snap == _lastSessionJson) return;
        _lastSessionJson = snap;
        SaveSession();
    }

    /// <summary>Desligado no modo de captura de tela, para não tocar nos arquivos do usuário.</summary>
    public bool PersistenceEnabled { get; set; } = true;

    public void SaveSession()
    {
        if (!PersistenceEnabled) return;
        if (Settings.RestoreTabs)
            Storage.SaveSession(Tabs.Select(t => new SavedTab(t.Title, t.Content, t.IsMain, t.FilePath, t.SavedContent, t == SelectedTab)));
        else Storage.ClearSession();
    }

    // ---------- Navegação ----------

    [RelayCommand] private void ToggleLibrary() => LibraryOpen = !LibraryOpen;
    [RelayCommand] private void ToggleConsole() => ConsoleOpen = !ConsoleOpen;
    [RelayCommand] private void OpenSettings() => SettingsRequested?.Invoke();

    // ---------- Biblioteca ----------

    // ---------- Configurações: gravação agrupada ----------

    private CancellationTokenSource? _saveCts;

    private async void ScheduleSettingsSave()
    {
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(400, cts.Token);
            Storage.SaveSettings(Settings);
        }
        catch (TaskCanceledException) { /* uma mudança mais nova reagendou */ }
    }

    /// <summary>Grava já (ao fechar o app), cancelando qualquer gravação agendada.</summary>
    public void FlushSettings()
    {
        _saveCts?.Cancel();
        if (PersistenceEnabled) Storage.SaveSettings(Settings);
    }

    public IReadOnlyList<Option<string>> PerformanceOptions { get; } =
    [
        new("Auto", "Auto (recommended)"), new("Normal", "Normal"), new("Lite", "Lite (low-end PC)"),
    ];

    // ---------- Biblioteca: leitura preguiçosa ----------

    private bool _libraryLoaded;

    /// <summary>Lê a lista de scripts na primeira vez que precisa (não atrasa a abertura do app).</summary>
    public void EnsureLibrary()
    {
        if (_libraryLoaded) return;
        _libraryLoaded = true;
        _library = _libraryProvider.GetAll().ToList();
        ApplyFilter();
    }

    partial void OnLibraryOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(SideOpen));
        if (!value) return;
        ClientsOpen = false; // Scripts e Clientes dividem o mesmo espaço
        EnsureLibrary();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredLibrary.Clear();
        foreach (var s in _library.Where(s => s.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)))
            FilteredLibrary.Add(s);
    }

    [RelayCommand]
    private void ReloadLibrary() => ReloadLibraryCore(announce: true);

    private void ReloadLibraryCore(bool announce)
    {
        _libraryLoaded = true;
        _library = _libraryProvider.GetAll().ToList();
        ApplyFilter();
        if (announce) ShowToast($"List refreshed · {_library.Count} script(s)");
    }

    [RelayCommand]
    private void OpenLibraryFolder()
    {
        if (LibraryFolder is not { } dir) return;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log(LogLevel.Error, $"Couldn't open the folder:{ex.Message}"); }
    }

    private bool IsInLibrary(string path) =>
        LibraryFolder is { } dir && Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void OpenScript(ScriptEntry entry)
    {
        if (entry.Path is not null) { OpenFromPath(entry.Path); return; }
        var existing = Tabs.FirstOrDefault(t => !t.IsMain && t.Title == entry.Name);
        if (existing is not null) { SelectedTab = existing; return; }
        var tab = new ScriptTab(entry.Name, entry.Content);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    // ---------- Arquivos ----------

    [RelayCommand] private void OpenFile() => OpenFileRequested?.Invoke();

    [RelayCommand]
    private async Task Save()
    {
        if (SelectedTab is { } tab && SaveHandler is { } h) await h(tab, false);
    }

    [RelayCommand]
    private async Task SaveAs()
    {
        if (SelectedTab is { } tab && SaveHandler is { } h) await h(tab, true);
    }

    /// <summary>Abre um arquivo de texto numa aba (ou seleciona a aba que já o tem aberto).</summary>
    public void OpenFromPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var existing = Tabs.FirstOrDefault(t => t.FilePath is { } p && string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { SelectedTab = existing; ShowToast($"Already open ·{existing.Title}"); return; }
            if (new FileInfo(full).Length > 5 * 1024 * 1024) { ShowToast("File too large (max 5 MB)"); return; }
            var tab = new ScriptTab(Path.GetFileName(full), File.ReadAllText(full), false, full);
            Tabs.Add(tab);
            SelectedTab = tab;
            ShowToast($"Opened ·{tab.Title}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Failed to open:{ex.Message}");
            ShowToast("Couldn't open the file");
        }
    }

    /// <summary>Grava a aba no caminho dado e a marca como salva.</summary>
    public bool WriteTab(ScriptTab tab, string path)
    {
        try
        {
            File.WriteAllText(path, tab.Content, new UTF8Encoding(false));
            tab.FilePath = path;
            if (!tab.IsMain) tab.Title = Path.GetFileName(path);
            tab.SavedContent = tab.Content;
            ShowToast($"Saved ·{Path.GetFileName(path)}");
            if (IsInLibrary(path)) ReloadLibraryCore(announce: false);
            return true;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Couldn't save:{ex.Message}");
            ConsoleOpen = true;
            ShowToast("Couldn't save");
            return false;
        }
    }

    // ---------- Abas ----------

    [RelayCommand]
    private void NewTab()
    {
        var n = 1;
        while (Tabs.Any(t => t.Title == $"Script {n}")) n++;
        var tab = new ScriptTab($"Script {n}", "");
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    private static bool NeedsConfirm(ScriptTab tab) => tab.IsDirty && !string.IsNullOrWhiteSpace(tab.Content);

    [RelayCommand]
    private void CloseTab(ScriptTab? tab)
    {
        tab ??= SelectedTab;
        if (tab is null || tab.IsMain) return;
        if (NeedsConfirm(tab)) { AskCloseConfirm(tab); return; }
        ForceClose(tab);
    }

    private void ForceClose(ScriptTab tab)
    {
        var i = Tabs.IndexOf(tab);
        if (i < 0) return;
        Tabs.Remove(tab);
        if (SelectedTab == tab || SelectedTab is null) SelectedTab = Tabs[Math.Min(i, Tabs.Count - 1)];
    }

    private void DuplicateTab(ScriptTab tab)
    {
        var baseName = tab.Title.EndsWith(" (copy)") ? tab.Title : tab.Title + " (copy)";
        var name = baseName; var n = 2;
        while (Tabs.Any(t => t.Title == name)) name = $"{baseName} {n++}";
        var copy = new ScriptTab(name, tab.Content);
        Tabs.Insert(Tabs.IndexOf(tab) + 1, copy);
        SelectedTab = copy;
    }

    private void CloseOthers(ScriptTab keep)
    {
        var kept = 0;
        foreach (var t in Tabs.Where(t => t != keep && !t.IsMain).ToList())
        {
            if (NeedsConfirm(t)) { kept++; continue; }
            Tabs.Remove(t);
        }
        SelectedTab = keep;
        if (kept > 0) ShowToast($"{kept} tab(s) with changes were kept");
    }

    // Renomear
    [RelayCommand]
    private void BeginRename(ScriptTab? tab)
    {
        tab ??= SelectedTab;
        if (tab is null || tab.IsMain) return;
        SelectedTab = tab;
        tab.EditTitle = tab.Title;
        tab.IsRenaming = true;
        RenameStarted?.Invoke(tab);
    }

    public void CommitRename(ScriptTab tab)
    {
        if (!tab.IsRenaming) return;
        var t = tab.EditTitle.Trim();
        tab.IsRenaming = false;
        if (t.Length > 0 && t != tab.Title) tab.Title = t;
    }

    public void CancelRename(ScriptTab tab) => tab.IsRenaming = false;

    /// <summary>Próxima/anterior aba (Ctrl+Tab), circular.</summary>
    public void CycleTab(int dir)
    {
        if (Tabs.Count < 2) return;
        var i = SelectedTab is null ? 0 : Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(i + dir + Tabs.Count) % Tabs.Count];
    }

    // ---------- Menu de contexto da aba (desenhado dentro da janela) ----------

    private ScriptTab? _menuTab;
    [ObservableProperty] private bool _tabMenuOpen;
    [ObservableProperty] private Thickness _tabMenuMargin;
    [ObservableProperty] private bool _menuCanEdit;

    public void OpenTabMenu(ScriptTab tab, double x, double y)
    {
        _menuTab = tab;
        MenuCanEdit = !tab.IsMain;
        TabMenuMargin = new Thickness(x, y, 0, 0);
        TabMenuOpen = true;
    }

    [RelayCommand] private void CloseTabMenu() => TabMenuOpen = false;

    [RelayCommand]
    private void MenuRename() { var t = _menuTab; TabMenuOpen = false; if (t is not null) BeginRename(t); }

    [RelayCommand]
    private void MenuDuplicate() { var t = _menuTab; TabMenuOpen = false; if (t is not null) DuplicateTab(t); }

    [RelayCommand]
    private async Task MenuSave()
    {
        var t = _menuTab; TabMenuOpen = false;
        if (t is not null && SaveHandler is { } h) await h(t, false);
    }

    [RelayCommand]
    private void MenuClose() { var t = _menuTab; TabMenuOpen = false; if (t is not null) CloseTab(t); }

    [RelayCommand]
    private void MenuCloseOthers() { var t = _menuTab; TabMenuOpen = false; if (t is not null) CloseOthers(t); }

    // ---------- Confirmação ao fechar aba com alterações ----------

    private ScriptTab? _confirmTab;
    [ObservableProperty] private bool _confirmOpen;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmText = "";

    private void AskCloseConfirm(ScriptTab tab)
    {
        _confirmTab = tab;
        ConfirmTitle = "Save changes?";
        ConfirmText = $"“{tab.Title}” has unsaved changes.";
        ConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmSave()
    {
        var t = _confirmTab;
        if (t is null) { ConfirmOpen = false; return; }
        var ok = SaveHandler is { } h && await h(t, false);
        if (!ok) return; // cancelou o diálogo de arquivo: mantém a confirmação aberta
        ConfirmOpen = false; _confirmTab = null;
        ForceClose(t);
    }

    [RelayCommand]
    private void ConfirmDiscard()
    {
        var t = _confirmTab; ConfirmOpen = false; _confirmTab = null;
        if (t is not null) ForceClose(t);
    }

    [RelayCommand]
    private void ConfirmCancel() { ConfirmOpen = false; _confirmTab = null; }

    // ---------- Paleta de comandos (Ctrl+K) ----------

    [ObservableProperty] private bool _paletteOpen;
    [ObservableProperty] private string _paletteQuery = "";
    [ObservableProperty] private PaletteCommand? _selectedPaletteItem;

    private void BuildCommands()
    {
        _commands =
        [
            new("New script", "Ctrl+T", () => NewTab()),
            new("Open file…", "Ctrl+O", () => OpenFileRequested?.Invoke()),
            new("Save", "Ctrl+S", () => _ = Save()),
            new("Save as…", "Ctrl+Shift+S", () => _ = SaveAs()),
            new("Execute script", "Ctrl+Enter", () => _ = Run()),
            new("Attach / Detach", "", () => _ = Attach()),
            new("Toggle Clients", "", () => ToggleClients()),
            new("Kill all Roblox", "", () => _ = KillCore(null)),
            new("Find in editor", "Ctrl+F", () => FindRequested?.Invoke()),
            new("Toggle comment", "Ctrl+/", () => ToggleCommentRequested?.Invoke()),
            new("Rename tab", "F2", () => BeginRename(null)),
            new("Close tab", "Ctrl+W", () => CloseTab(null)),
            new("Next tab", "Ctrl+Tab", () => CycleTab(1)),
            new("Toggle Scripts", "Ctrl+B", () => ToggleLibrary()),
            new("Toggle Output", "Ctrl+J", () => ToggleConsole()),
            new("Refresh scripts", "", () => ReloadLibraryCore(announce: true)),
            new("Open scripts folder", "", () => OpenLibraryFolder()),
            new("Clear editor", "", () => ClearEditor()),
            new("Clear output", "", () => ClearConsole()),
            new("Copy output", "", () => CopyConsoleRequested?.Invoke()),
            new("Increase font", "Ctrl +", () => Zoom(+1)),
            new("Decrease font", "Ctrl -", () => Zoom(-1)),
            new("Reset font", "Ctrl 0", () => ZoomReset()),
            new("Settings", "Ctrl+,", () => SettingsRequested?.Invoke()),
        ];
    }

    public void OpenPalette()
    {
        TabMenuOpen = false;
        PaletteQuery = "";
        RefreshPalette();
        PaletteOpen = true;
        PaletteOpened?.Invoke();
    }

    [RelayCommand] public void ClosePalette() => PaletteOpen = false;

    partial void OnPaletteQueryChanged(string value) => RefreshPalette();

    private void RefreshPalette()
    {
        var terms = PaletteQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        PaletteItems.Clear();
        foreach (var c in _commands.Where(c => terms.All(t => c.Title.Contains(t, StringComparison.OrdinalIgnoreCase))))
            PaletteItems.Add(c);
        SelectedPaletteItem = PaletteItems.FirstOrDefault();
    }

    public void MovePalette(int dir)
    {
        if (PaletteItems.Count == 0) return;
        var i = SelectedPaletteItem is null ? -1 : PaletteItems.IndexOf(SelectedPaletteItem);
        SelectedPaletteItem = PaletteItems[(i + dir + PaletteItems.Count) % PaletteItems.Count];
    }

    public void RunPaletteItem(PaletteCommand? item = null)
    {
        item ??= SelectedPaletteItem;
        PaletteOpen = false;
        item?.Run();
    }

    // ---------- Zoom do editor ----------

    public void Zoom(int dir)
    {
        var cur = Settings.EditorFontSize;
        var next = dir > 0
            ? FontSizes.Where(s => s > cur).DefaultIfEmpty(cur).Min()
            : FontSizes.Where(s => s < cur).DefaultIfEmpty(cur).Max();
        if (next == cur) return;
        Settings.EditorFontSize = next;
        ShowToast($"Fonte {next:0}");
    }

    public void ZoomReset()
    {
        Settings.EditorFontSize = new AppSettings().EditorFontSize;
        ShowToast($"Fonte {Settings.EditorFontSize:0}");
    }

    // ---------- Toast ----------

    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _toastVisible;
    private CancellationTokenSource? _toastCts;

    public async void ShowToast(string text)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        ToastText = text;
        ToastVisible = true;
        try
        {
            await Task.Delay(2200, cts.Token);
            ToastVisible = false;
        }
        catch (TaskCanceledException) { /* um toast mais novo assumiu */ }
    }

    // ---------- Arrastar e soltar ----------

    [ObservableProperty] private bool _dropHint;

    // ---------- Editor / Output ----------

    [RelayCommand]
    private void ClearEditor()
    {
        if (SelectedTab is null) return;
        SelectedTab.Content = "";
        EditorReloadRequested?.Invoke();
    }

    // Filtro do Output (chips)
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarn = true;
    [ObservableProperty] private bool _showError = true;

    public int WarnCount => _allLog.Count(l => l.Level == nameof(LogLevel.Warning));
    public int ErrorCount => _allLog.Count(l => l.Level == nameof(LogLevel.Error));

    partial void OnShowInfoChanged(bool value) => RebuildConsole();
    partial void OnShowWarnChanged(bool value) => RebuildConsole();
    partial void OnShowErrorChanged(bool value) => RebuildConsole();

    [RelayCommand] private void ToggleInfo() => ShowInfo = !ShowInfo;
    [RelayCommand] private void ToggleWarn() => ShowWarn = !ShowWarn;
    [RelayCommand] private void ToggleError() => ShowError = !ShowError;

    private bool Passes(ConsoleItem i) => i.Level switch
    {
        nameof(LogLevel.Warning) => ShowWarn,
        nameof(LogLevel.Error) => ShowError,
        _ => ShowInfo,
    };

    private void RebuildConsole()
    {
        Console.Clear();
        foreach (var i in _allLog.Where(Passes)) Console.Add(i);
    }

    [RelayCommand]
    private void ClearConsole()
    {
        _allLog.Clear();
        Console.Clear();
        OnPropertyChanged(nameof(WarnCount));
        OnPropertyChanged(nameof(ErrorCount));
    }

    /// <summary>A janela copia o texto do Output (visível) para a área de transferência.</summary>
    public event Action? CopyConsoleRequested;
    [RelayCommand] private void CopyConsole() => CopyConsoleRequested?.Invoke();
    public string ConsoleText => string.Join(Environment.NewLine, Console.Select(c => $"{c.Time}  {c.Text}"));

    // ---------- Attach / Executar ----------

    [RelayCommand]
    private async Task Attach()
    {
        if (IsAttaching) return;
        if (IsAttached)
        {
            _session.Detach();
            IsAttached = false;
            Log(LogLevel.Info, "Detached.");
            OnPropertyChanged(nameof(Status));
            return;
        }
        if (!_session.IsTargetRunning)
        {
            IsTargetRunning = false;
            Log(LogLevel.Warning, "Roblox isn't open. Open the game and try again.");
            ShowToast("Abra o Roblox primeiro");
            return;
        }
        IsAttaching = true;
        HasError = false;
        try
        {
            foreach (var l in await _session.AttachAsync())
                Log(l.Level, l.Text, l.Time);
            IsAttached = _session.IsAttached;
            HasError = !IsAttached; // não anexou: vermelho até a próxima tentativa
        }
        catch (Exception ex) { Log(LogLevel.Error, ex.Message); HasError = true; }
        finally
        {
            IsAttaching = false;
            OnPropertyChanged(nameof(Status));
            _ = RefreshClients();
        }
    }

    [RelayCommand]
    private async Task Run()
    {
        if (SelectedTab is null || IsRunning) return;
        if (!IsAttached)
        {
            Log(LogLevel.Warning, "Attach before executing.");
            ShowToast("Attach before executing");
            return;
        }
        IsRunning = true;
        ConsoleOpen = true;
        try
        {
            // clientes marcados no painel = alvo; nenhum marcado = todos os anexados
            foreach (var l in await _session.RunOnAsync(SelectedTab.Content, SelectedPids()))
                Log(l.Level, l.Text, l.Time);
        }
        catch (Exception ex) { Log(LogLevel.Error, ex.Message); }
        finally
        {
            IsRunning = false;
            if (!_session.IsAttached) IsAttached = false; // o alvo pode ter fechado durante a execução
            OnPropertyChanged(nameof(Status));
        }
    }

    // ---------- Configurações ----------
    [RelayCommand] private void ResetSettings() { Settings.CopyFrom(new AppSettings()); Services.ThemeManager.CleanOldImages(null); }

    [RelayCommand]
    private void ClearSavedSession()
    {
        Storage.ClearSession();
        Log(LogLevel.Info, "Saved session cleared.");
    }

    /// <summary>Teto de linhas no Output: sessões longas não crescem a memória nem a lista na tela.</summary>
    internal const int MaxLogLines = 1000;

    internal void Log(LogLevel level, string text, DateTime? time = null)
    {
        var item = new ConsoleItem((time ?? DateTime.Now).ToString("HH:mm:ss"), text, level.ToString());
        _allLog.Add(item);
        // poda em lotes (a cada 200 linhas além do teto) para não reconstruir a lista a cada linha
        if (_allLog.Count > MaxLogLines + 200)
        {
            _allLog.RemoveRange(0, _allLog.Count - MaxLogLines);
            RebuildConsole();
        }
        else if (Passes(item)) Console.Add(item);
        OnPropertyChanged(nameof(WarnCount));
        OnPropertyChanged(nameof(ErrorCount));
    }
}
