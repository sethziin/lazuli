using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lazuli.Contracts;
using Lazuli.UI.Services;

namespace Lazuli.UI.ViewModels;

/// <summary>Um jogo jogado recentemente (Home).</summary>
public partial class RecentGame : ObservableObject
{
    public RecentGame(RobloxJoin join) { Join = join; _name = $"Place {join.PlaceId}"; }

    public RobloxJoin Join { get; private set; }
    public long PlaceId => Join.PlaceId;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _creator = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIcon))] private Bitmap? _icon;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPlaying))] private string _playingText = "";

    public bool HasPlaying => PlayingText.Length > 0;

    public bool HasIcon => Icon is not null;
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string PlayedText => Ago(Join.Time);

    public void Update(RobloxJoin join)
    {
        Join = join;
        OnPropertyChanged(nameof(PlayedText));
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Initial));

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        return $"{(int)d.TotalDays} days ago";
    }
}

public partial class MainViewModel
{
    /// <summary>Home é a tela inicial; alterna com o Editor pelos botões da barra lateral.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EditorActive))] private bool _homeOpen = true;

    /// <summary>Layout escolhido ao abrir o app (trocar nas configurações só vale ao reiniciar).</summary>
    public bool IsSimple { get; private set; }

    /// <summary>true quando a tela à mostra é o editor (nenhuma Home por cima).</summary>
    public bool EditorActive => !HomeOpen && !HubOpen && !AutoExecOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLastGame), nameof(NoGames))] private RecentGame? _lastGame;
    [ObservableProperty] private bool _homeLoading;

    /// <summary>Outros jogos recentes (sem o último, que tem o destaque).</summary>
    public ObservableCollection<RecentGame> RecentGames { get; } = new();

    public bool HasLastGame => LastGame is not null;
    public bool NoGames => LastGame is null && !HomeLoading;
    public bool HasRecentGames => RecentGames.Count > 0;
    public string BackendLabel => Updates.AppInfo.BackendName;

    private DateTime _homeLoadedAt = DateTime.MinValue;

    private void ApplyLayout()
    {
        if (!IsSimple) return;
        HomeOpen = false;
        LibraryOpen = false;
        ClientsOpen = false;
    }

    /// <summary>LAZULI_LAYOUT=simple|detailed força o layout só nesta execução (testes).</summary>
    private void ApplyLayoutOverride()
    {
        if (Environment.GetEnvironmentVariable("LAZULI_LAYOUT") is { } l && (l == "simple" || l == "detailed")) IsSimple = l == "simple";
    }

    [RelayCommand] private void ShowHome() { if (IsSimple) return; ClosePages(); HomeOpen = true; }
    [RelayCommand] private void ShowEditor() { ClosePages(); HomeOpen = false; }

    partial void OnSelectedTabChanged(ScriptTab? value)
    {
        if (_homeReady) { ClosePages(); HomeOpen = false; } // a seleção inicial (RestoreSession) não esconde a Home
    }

    private bool _homeReady;

    /// <summary>Chamado ao abrir a janela: a partir daqui, trocar de aba sai da Home.</summary>
    public void StartHome()
    {
        _homeReady = true;
        if (HomeOpen) _ = RefreshHome();
        // com o Início à mostra, atualiza sozinho a cada 15 s (nomes/ícones vêm do cache, só os logs são relidos)
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => { if (HomeOpen) _ = RefreshHome(); };
        timer.Start();
    }

    partial void OnHomeOpenChanged(bool value)
    {
        if (value && DateTime.UtcNow - _homeLoadedAt > TimeSpan.FromSeconds(30)) _ = RefreshHome();
    }

    partial void OnHomeLoadingChanged(bool value) => OnPropertyChanged(nameof(NoGames));

    [RelayCommand]
    private async Task RefreshHome()
    {
        if (HomeLoading) return;
        HomeLoading = true;
        try
        {
            var joins = await Task.Run(() => RobloxRecent.ReadJoins());
            _homeLoadedAt = DateTime.UtcNow;
            // um por jogo, o mais recente de cada
            var latest = joins.GroupBy(j => j.PlaceId).Select(g => g.First()).Take(7).ToList();

            var games = new List<RecentGame>();
            foreach (var j in latest)
            {
                var existing = (LastGame?.PlaceId == j.PlaceId ? LastGame : RecentGames.FirstOrDefault(r => r.PlaceId == j.PlaceId));
                if (existing is not null) { existing.Update(j); games.Add(existing); }
                else games.Add(new RecentGame(j));
            }

            LastGame = games.FirstOrDefault();
            RecentGames.Clear();
            foreach (var g in games.Skip(1)) RecentGames.Add(g);
            OnPropertyChanged(nameof(HasRecentGames));

            foreach (var g in games) await LoadGameInfo(g);
        }
        catch (Exception ex) { Log(LogLevel.Warning, $"Home: couldn't read recent games ({ex.Message})."); }
        finally { HomeLoading = false; }
    }

    private static async Task LoadGameInfo(RecentGame g)
    {
        try
        {
            var universe = g.Join.UniverseId ?? await RobloxGames.UniverseOfAsync(g.PlaceId, CancellationToken.None);
            if (universe is null) return;
            var info = await RobloxGames.GetAsync(universe.Value, CancellationToken.None);
            if (info is null) return;
            g.Name = info.Name;
            g.Creator = info.Creator.Length > 0 ? $"by {info.Creator}" : "";
            g.PlayingText = info.Playing > 0 ? $"{info.Playing:N0} playing" : "";
            if (info.IconPath is not null && g.Icon is null)
            {
                var path = info.IconPath;
                g.Icon = await Task.Run(() => new Bitmap(path));
            }
        }
        catch { /* sem rede ou resposta inesperada: fica com o placeId */ }
    }

    [RelayCommand]
    private void OpenRoblox()
    {
        try { RobloxRecent.OpenRoblox(); ShowToast("Opening Roblox…"); }
        catch { Log(LogLevel.Error, "Couldn't open Roblox (is it installed?)."); }
    }

    [RelayCommand]
    private void JoinGame(RecentGame? game)
    {
        if (game is null) return;
        try { RobloxRecent.Join(game.PlaceId); ShowToast($"Joining {game.Name}…"); }
        catch { Log(LogLevel.Error, "Couldn't open Roblox (is it installed?)."); }
    }

    [RelayCommand]
    private void JoinServer(RecentGame? game)
    {
        if (game is null) return;
        try { RobloxRecent.Join(game.PlaceId, game.Join.JobId); ShowToast("Rejoining the same server…"); }
        catch { Log(LogLevel.Error, "Couldn't open Roblox (is it installed?)."); }
    }

    [RelayCommand]
    private void NewScriptFromHome()
    {
        HomeOpen = false;
        NewTabCommand.Execute(null);
    }

    // ---------- Manutenção (faxina de arquivos temporários da máquina do usuário) ----------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CleanLabel))] private bool _cleanArmed;
    private CancellationTokenSource? _cleanCts;

    public string CleanLabel => CleanArmed ? "Confirm?" : "Clean Roblox launch";

    /// <summary>1º clique arma (3 s); 2º fecha o Roblox, limpa logs do Roblox e os arquivos de trabalho do Lazuli, e reabre.</summary>
    [RelayCommand]
    private async Task StartRobloxClean()
    {
        if (!CleanArmed)
        {
            CleanArmed = true;
            _cleanCts?.Cancel();
            var cts = _cleanCts = new CancellationTokenSource();
            _ = DisarmCleanLater(cts.Token);
            return;
        }
        _cleanCts?.Cancel();
        CleanArmed = false;

        var res = await Task.Run(Services.Maintenance.StartRobloxClean);
        Log(LogLevel.Success, $"Roblox relaunched clean. {res.Files} file(s) removed ({res.Human}).");
        ShowToast("Roblox relaunched clean");
        // os ícones/nomes voltam a ser buscados; recarrega a Home
        RecentGames.Clear();
        LastGame = null;
        OnPropertyChanged(nameof(HasRecentGames));
    }

    private async Task DisarmCleanLater(CancellationToken ct)
    {
        try { await Task.Delay(3000, ct); CleanArmed = false; }
        catch (TaskCanceledException) { }
    }

    /// <summary>Só a faxina dos arquivos do Lazuli (sem mexer no Roblox).</summary>
    [RelayCommand]
    private async Task CleanAppData()
    {
        var res = await Task.Run(Services.Maintenance.CleanAppData);
        Log(LogLevel.Success, $"App data cleared: {res.Files} file(s) ({res.Human}).");
        ShowToast("App data cleared");
    }
}
