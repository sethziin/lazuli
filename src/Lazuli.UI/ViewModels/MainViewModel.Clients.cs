using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lazuli.Contracts;

namespace Lazuli.UI.ViewModels;

/// <summary>Um Roblox aberto, como aparece no gerenciador de clientes.</summary>
public partial class RobloxClient : ObservableObject
{
    public RobloxClient(int pid) => Pid = pid;

    public int Pid { get; }
    public string PidText => $"PID {Pid}";

    [ObservableProperty] private string _title = "Roblox";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _tip = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusBrush))] private bool _isAttached;

    /// <summary>Marcado = alvo do Executar. Nenhum marcado = todos os anexados.</summary>
    [ObservableProperty] private bool _isSelected;

    public string StatusBrush => IsAttached ? "#3FB950" : "#E0B458";

    public void Update(ClientInfo i)
    {
        Title = i.Title; Detail = i.Detail; Tip = i.Tip; IsAttached = i.Attached;
    }
}

// Gerenciador de clientes (Roblox abertos): lista, escolher onde executar e encerrar.
public partial class MainViewModel
{
    public ObservableCollection<RobloxClient> Clients { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SideOpen))] private bool _clientsOpen;

    /// <summary>Barra lateral aberta (Scripts ou Clientes): decide a linha divisória.</summary>
    public bool SideOpen => LibraryOpen || ClientsOpen;

    public bool HasClients => Clients.Count > 0;
    public bool NoClients => Clients.Count == 0;

    /// <summary>Resumo de onde o Executar vai agir.</summary>
    public string TargetSummary
    {
        get
        {
            if (Clients.Count == 0) return "No Roblox open";
            var sel = Clients.Count(c => c.IsSelected);
            if (sel > 0) return sel == 1 ? "Executar em 1 cliente escolhido" : $"Executar em {sel} clientes escolhidos";
            var att = Clients.Count(c => c.IsAttached);
            return att == 0 ? "Attach to execute" : att == 1 ? "Executes on the attached client" : $"Executes on all {att} attached";
        }
    }

    public string ClientsCountText => Clients.Count == 1 ? "1 open" : $"{Clients.Count} open";

    [RelayCommand] private void ToggleClients() => ClientsOpen = !ClientsOpen;

    partial void OnClientsOpenChanged(bool value)
    {
        if (!value) return;
        LibraryOpen = false; // as duas barras dividem o mesmo espaço
        _ = RefreshClients();
    }

    private int _refreshing;

    /// <summary>Lê a lista do backend (fora da thread da tela) e atualiza a coleção sem piscar: só mexe no que mudou.</summary>
    private async Task RefreshClients()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            IReadOnlyList<ClientInfo> list;
            try { list = await Task.Run(() => _session.GetClients()); }
            catch { return; }

            var alive = list.Select(c => c.Pid).ToHashSet();
            for (var i = Clients.Count - 1; i >= 0; i--)
                if (!alive.Contains(Clients[i].Pid))
                {
                    Clients[i].PropertyChanged -= OnClientChanged;
                    Clients.RemoveAt(i);
                }
            foreach (var info in list)
            {
                var c = Clients.FirstOrDefault(x => x.Pid == info.Pid);
                if (c is null)
                {
                    c = new RobloxClient(info.Pid);
                    c.PropertyChanged += OnClientChanged;
                    Clients.Add(c);
                }
                var wasAttached = c.IsAttached;
                c.Update(info);
                if (Settings.AutoSelectClients && info.Attached && !wasAttached && Backend.Has(BackendCapabilities.PerClientRun))
                    c.IsSelected = true;
            }
            NotifyClientsChanged();
        }
        finally { System.Threading.Volatile.Write(ref _refreshing, 0); }
    }

    private void OnClientChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RobloxClient.IsSelected) or nameof(RobloxClient.IsAttached)) OnPropertyChanged(nameof(TargetSummary));
    }

    private void NotifyClientsChanged()
    {
        OnPropertyChanged(nameof(HasClients));
        OnPropertyChanged(nameof(NoClients));
        OnPropertyChanged(nameof(ClientsCountText));
        OnPropertyChanged(nameof(TargetSummary));
    }

    /// <summary>PIDs escolhidos como alvo, ou null (= todos os anexados).</summary>
    private int[]? SelectedPids()
    {
        if (!Backend.Has(BackendCapabilities.PerClientRun)) return null; // o backend não escolhe clientes: executa em todos os anexados
        var sel = Clients.Where(c => c.IsSelected).Select(c => c.Pid).ToArray();
        return sel.Length > 0 ? sel : null;
    }

    [RelayCommand] private async Task RefreshClientsNow() => await RefreshClients();

    [RelayCommand]
    private void SelectAllClients()
    {
        var all = Clients.All(c => c.IsSelected);
        foreach (var c in Clients) c.IsSelected = !all;
    }

    // ---------- encerrar ----------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(KillLabel))] private bool _killArmed;
    private CancellationTokenSource? _killCts;

    public string KillLabel => KillArmed ? "Confirm?" : "Kill Roblox";

    /// <summary>
    /// Botão "Encerrar Roblox": o primeiro clique arma (vira "Confirmar?" por 3 s); o segundo encerra todos.
    /// Evita fechar um jogo por clique sem querer, sem abrir uma janela de confirmação.
    /// </summary>
    [RelayCommand]
    private async Task KillAll()
    {
        if (!KillArmed)
        {
            if (!_session.IsTargetRunning && Clients.Count == 0) { ShowToast("Roblox is already closed"); return; }
            KillArmed = true;
            _killCts?.Cancel();
            _ = DisarmKillLater((_killCts = new CancellationTokenSource()).Token); // sem await: o comando não pode ficar "em execução" (desabilitaria o 2º clique)
            return;
        }
        _killCts?.Cancel();
        KillArmed = false;
        await KillCore(null);
    }

    private async Task DisarmKillLater(CancellationToken ct)
    {
        try { await Task.Delay(3000, ct); KillArmed = false; }
        catch (TaskCanceledException) { /* confirmou ou foi rearmado */ }
    }

    /// <summary>X no cartão: encerra só aquele cliente.</summary>
    [RelayCommand]
    private async Task KillClient(RobloxClient? client)
    {
        if (client is null) return;
        await KillCore([client.Pid]);
    }

    private async Task KillCore(IReadOnlyCollection<int>? pids)
    {
        try
        {
            var lines = await _session.KillAsync(pids);
            foreach (var l in lines) Log(l.Level, l.Text, l.Time);
            ShowToast(pids is null ? "Roblox encerrado" : "Cliente encerrado");
        }
        catch (Exception ex) { Log(LogLevel.Error, ex.Message); }
        await RefreshClients();
        if (IsAttached && !_session.IsAttached) IsAttached = false;
        OnPropertyChanged(nameof(Status));
    }
}
