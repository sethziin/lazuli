using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lazuli.Contracts;
using Lazuli.UI.Services;

namespace Lazuli.UI.ViewModels;

/// <summary>Um cartão do Script Hub.</summary>
public partial class HubItem : ObservableObject
{
    public HubItem(SbScript s)
    {
        Script = s;
        ViewsText = ScriptBlox.ViewsText(s.Views);
        Initial = s.Title.Length > 0 ? s.Title[..1].ToUpperInvariant() : "?";
    }

    public SbScript Script { get; }
    public string Title => Script.Title;
    public string Game => Script.Game.Length > 0 ? Script.Game : (Script.Universal ? "Universal" : "");
    public string ViewsText { get; }
    public string Initial { get; }
    public bool Verified => Script.Verified;
    public bool NeedsKey => Script.Key;
    public bool IsUniversal => Script.Universal;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImage))] private Bitmap? _image;
    public bool HasImage => Image is not null;
}

/// <summary>Uma categoria (aba) do Hub.</summary>
public partial class HubCategory : ObservableObject
{
    public HubCategory(string id, string name) { Id = id; Name = name; }
    public string Id { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Script Hub: scripts do ScriptBlox com categorias, busca, "carregar mais" e miniaturas.</summary>
public partial class MainViewModel
{
    public ObservableCollection<HubCategory> HubCategories { get; } =
    [
        new("latest", "Latest"), new("trending", "Trending"), new("popular", "Popular"),
        new("verified", "Verified"), new("universal", "Universal"), new("nokey", "No key"),
    ];

    public ObservableCollection<HubItem> HubItems { get; } = new();

    [ObservableProperty] private string _hubQuery = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HubShowEmpty), nameof(HubShowSpinner), nameof(HubShowMore), nameof(HubLoadingMore))] private bool _hubLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HubHasError), nameof(HubShowEmpty), nameof(HubShowSpinner))] private string _hubError = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HubShowMore))] private bool _hubHasMore;

    public bool HubHasError => HubError.Length > 0;
    public bool HubShowSpinner => HubLoading && HubItems.Count == 0;
    public bool HubShowEmpty => !HubLoading && !HubHasError && HubItems.Count == 0 && _hubLoadedOnce;
    public bool HubShowMore => HubHasMore && !HubLoading;
    public bool HubLoadingMore => HubLoading && HubItems.Count > 0;

    private HubCategory SelectedHubCategory => HubCategories.First(c => c.IsSelected);
    private CancellationTokenSource? _hubCts;
    private CancellationTokenSource? _hubDebounce;
    private int _hubPage = 1;
    private bool _hubLoadedOnce;
    private int _hubRequest;

    private void EnsureHubLoaded()
    {
        if (!HubCategories.Any(c => c.IsSelected)) HubCategories[0].IsSelected = true;
        if (!_hubLoadedOnce && !HubLoading) _ = LoadHub(reset: true);
    }

    [RelayCommand]
    private void SelectHubCategory(HubCategory? c)
    {
        if (c is null || c.IsSelected && _hubLoadedOnce && !HubHasError) return;
        foreach (var x in HubCategories) x.IsSelected = x == c;
        _ = LoadHub(reset: true);
    }

    [RelayCommand] private Task RefreshHub() => LoadHub(reset: true);
    [RelayCommand] private Task LoadMoreHub() => LoadHub(reset: false);

    /// <summary>A busca espera o usuário parar de digitar (0,45 s) antes de ir à internet.</summary>
    partial void OnHubQueryChanged(string value)
    {
        if (!HubOpen && !_hubLoadedOnce) return;
        _hubDebounce?.Cancel();
        var cts = _hubDebounce = new CancellationTokenSource();
        _ = DebounceHub(cts.Token);
    }

    private async Task DebounceHub(CancellationToken ct)
    {
        try { await Task.Delay(450, ct); await LoadHub(reset: true); }
        catch (OperationCanceledException) { }
    }

    private async Task LoadHub(bool reset)
    {
        if (!HubCategories.Any(c => c.IsSelected)) HubCategories[0].IsSelected = true;
        if (!reset && (HubLoading || !HubHasMore)) return;

        _hubCts?.Cancel(); // uma busca nova cancela a anterior
        var cts = _hubCts = new CancellationTokenSource();
        var request = ++_hubRequest;
        var page = reset ? 1 : _hubPage + 1;

        HubError = "";
        HubLoading = true;
        if (reset)
        {
            var old = HubItems.ToList();
            HubItems.Clear(); HubHasMore = false; OnPropertyChanged(nameof(HubShowSpinner));
            // libera a memória das miniaturas antigas (depois de a tela largar os cartões)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { foreach (var o in old) { var im = o.Image; o.Image = null; im?.Dispose(); } }, Avalonia.Threading.DispatcherPriority.Background);
        }
        try
        {
            var result = await ScriptBlox.GetAsync(new ScriptBlox.Query(HubQuery, SelectedHubCategory.Id, page), cts.Token);
            if (request != _hubRequest) return; // chegou depois de uma busca mais nova

            _hubPage = page;
            var known = HubItems.Select(i => i.Script.Id).ToHashSet();
            var added = new List<HubItem>();
            foreach (var s in result.Items)
                if (known.Add(s.Id)) { var it = new HubItem(s); HubItems.Add(it); added.Add(it); }
            HubHasMore = result.HasMore;
            _hubLoadedOnce = true;
            OnPropertyChanged(nameof(HubLoadingMore));
            _ = LoadHubImages(added, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (SbException ex) { if (request == _hubRequest) HubError = ex.Message; }
        catch (Exception ex)
        {
            if (request == _hubRequest) HubError = "Something went wrong loading scripts.";
            Log(LogLevel.Warning, $"Script Hub: {ex.Message}");
        }
        finally
        {
            if (request == _hubRequest)
            {
                HubLoading = false;
                _hubLoadedOnce = true;
                OnPropertyChanged(nameof(HubShowEmpty));
                OnPropertyChanged(nameof(HubShowSpinner));
            }
        }
    }

    private static async Task LoadHubImages(List<HubItem> items, CancellationToken ct)
    {
        await Task.WhenAll(items.Select(async it =>
        {
            try
            {
                var file = await ScriptBlox.GetImageFileAsync(it.Script.ImageUrl, ct);
                if (file is null || ct.IsCancellationRequested) return;
                var bmp = await Task.Run(() => { using var fs = File.OpenRead(file); return Bitmap.DecodeToWidth(fs, 360); }, ct);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => it.Image = bmp);
            }
            catch { /* sem miniatura: o cartão mostra a inicial */ }
        }));
    }

    [RelayCommand]
    private async Task ExecuteHubItem(HubItem? item)
    {
        if (item is null) return;
        if (!IsAttached) { Log(LogLevel.Warning, "Attach before executing."); ShowToast("Attach before executing"); return; }
        if (IsRunning) return;
        IsRunning = true;
        ConsoleOpen = true;
        try
        {
            Log(LogLevel.Info, $"Executing \"{item.Title}\"…");
            foreach (var l in await _session.RunOnAsync(item.Script.Code, SelectedPids()))
                Log(l.Level, l.Text, l.Time);
        }
        catch (Exception ex) { Log(LogLevel.Error, ex.Message); }
        finally
        {
            IsRunning = false;
            if (!_session.IsAttached) IsAttached = false;
            OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>Abre o script numa aba nova do editor (para ler, editar ou salvar).</summary>
    [RelayCommand]
    private void OpenHubItem(HubItem? item)
    {
        if (item is null) return;
        var title = item.Title.Length > 28 ? item.Title[..28].TrimEnd() + "…" : item.Title;
        var tab = new ScriptTab(title, item.Script.Code);
        Tabs.Add(tab);
        SelectedTab = tab;
        ClosePages();
        HomeOpen = false;
        ShowToast("Opened in a new tab");
    }
}
