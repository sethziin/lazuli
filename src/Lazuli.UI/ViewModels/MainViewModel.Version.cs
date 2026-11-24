using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lazuli.Contracts;
using Lazuli.UI.Services;

namespace Lazuli.UI.ViewModels;

/// <summary>
/// Versão do Roblox: quando o backend diz qual versão suporta e o Roblox aberto é outra, oferece baixar a suportada
/// (da CDN oficial, numa pasta do Lazuli) e abrir o Roblox nela. Serve para downgrade e upgrade.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _versionOpen;
    [ObservableProperty] private string _versionTitle = "";
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _versionPrimaryLabel = "";
    [ObservableProperty] private bool _versionCanPrimary;
    [ObservableProperty] private bool _versionBusy;
    [ObservableProperty] private double _versionProgress;
    [ObservableProperty] private string _versionProgressText = "";
    [ObservableProperty] private string _versionError = "";

    private string? _supportedVersion;
    private CancellationTokenSource? _versionCts;
    private readonly HashSet<string> _versionPrompted = new(StringComparer.OrdinalIgnoreCase);

    public string VersionCloseLabel => VersionBusy ? "Cancel" : "OK";
    partial void OnVersionBusyChanged(bool value) => OnPropertyChanged(nameof(VersionCloseLabel));

    /// <summary>Configurações → Roblox version: abre o diálogo mesmo sem diferença de versão.</summary>
    [RelayCommand]
    private async Task OpenRobloxVersion()
    {
        VersionError = "";
        VersionTitle = "Roblox version";
        VersionText = "Checking which version is supported…";
        VersionCanPrimary = false;
        VersionOpen = true;
        await LoadSupportedVersion();
        ComposeVersionDialog(mismatch: false);
    }

    /// <summary>Depois de um Attach que não deu certo: se o Roblox aberto não é a versão suportada, mostra o aviso (uma vez por versão).</summary>
    private async Task CheckVersionAfterAttach()
    {
        try
        {
            if (VersionOpen) return;
            await LoadSupportedVersion();
            if (_supportedVersion is null) return;
            var running = RobloxDeployer.RunningVersions();
            var wrong = running.FirstOrDefault(v => !v.Equals(_supportedVersion, StringComparison.OrdinalIgnoreCase));
            if (wrong is null || !_versionPrompted.Add(wrong)) return;
            VersionError = "";
            ComposeVersionDialog(mismatch: true);
            VersionOpen = true;
        }
        catch { /* aviso opcional: nunca atrapalha o Attach */ }
    }

    private async Task LoadSupportedVersion()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var v = await _session.GetSupportedTargetVersionAsync(cts.Token);
            if (RobloxDeployer.IsValidVersion(v)) _supportedVersion = v!.ToLowerInvariant();
        }
        catch { /* sem rede: fica com o que já sabia */ }
    }

    private void ComposeVersionDialog(bool mismatch)
    {
        var supported = _supportedVersion;
        if (supported is null)
        {
            VersionTitle = "Roblox version";
            VersionText = "The current backend doesn't say which Roblox version it supports (or it couldn't be checked right now).";
            VersionCanPrimary = false;
            return;
        }
        var running = RobloxDeployer.RunningVersions();
        var wrong = running.FirstOrDefault(v => !v.Equals(supported, StringComparison.OrdinalIgnoreCase));
        var installed = RobloxDeployer.IsInstalled(supported);

        VersionTitle = mismatch || wrong is not null ? "Version mismatch" : "Roblox version";
        VersionText = wrong is not null
            ? $"Your Roblox client ({wrong}) isn't supported by this build.\n\nSupported client: {supported}"
            : $"Supported client: {supported}" + (running.Count > 0 ? "\nThe open Roblox already matches." : "");
        if (installed) VersionText += "\n\nThis version is already downloaded.";
        VersionPrimaryLabel = !installed ? "Install supported version"
            : wrong is not null ? "Restart Roblox on it" : "Open Roblox on it";
        VersionCanPrimary = true;
    }

    [RelayCommand]
    private async Task VersionPrimary()
    {
        var supported = _supportedVersion;
        if (supported is null || VersionBusy) return;
        VersionError = "";
        if (!RobloxDeployer.IsInstalled(supported))
        {
            VersionBusy = true;
            VersionProgress = 0;
            VersionProgressText = "Starting…";
            _versionCts = new CancellationTokenSource();
            var progress = new Progress<(string Text, double Fraction)>(p =>
            {
                VersionProgressText = p.Text;
                VersionProgress = Math.Clamp(p.Fraction * 100, 0, 100);
            });
            try
            {
                Log(LogLevel.Info, $"Downloading Roblox {supported} from Roblox's servers…");
                await RobloxDeployer.InstallAsync(supported, progress, _versionCts.Token);
                Log(LogLevel.Success, $"Roblox {supported} is ready.");
            }
            catch (OperationCanceledException) when (_versionCts.IsCancellationRequested)
            {
                Log(LogLevel.Warning, "Roblox download canceled.");
                VersionBusy = false;
                return;
            }
            catch (Exception ex)
            {
                VersionError = ex.Message;
                Log(LogLevel.Error, "Roblox download failed: " + ex.Message);
                VersionBusy = false;
                return;
            }
            finally { _versionCts?.Dispose(); _versionCts = null; }
            VersionBusy = false;
            ComposeVersionDialog(mismatch: false);
            return;
        }

        try
        {
            if (RobloxDeployer.RunningVersions().Any(v => !v.Equals(supported, StringComparison.OrdinalIgnoreCase)))
            {
                if (IsAttached) { _session.Detach(); IsAttached = false; }
                await RobloxDeployer.CloseRunningAsync();
            }
            RobloxDeployer.Launch(supported);
            Log(LogLevel.Info, $"Opening Roblox {supported}. Join a game, then attach.");
            VersionOpen = false;
            ShowToast("Opening Roblox…");
            _ = RefreshClients();
        }
        catch (Exception ex)
        {
            VersionError = "Couldn't open Roblox: " + ex.Message;
            Log(LogLevel.Error, VersionError);
        }
    }

    [RelayCommand]
    private void VersionClose()
    {
        if (VersionBusy) { _versionCts?.Cancel(); return; }
        VersionOpen = false;
    }
}
