using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Lazuli.UI.Services;
using Lazuli.UI.Updates;

namespace Lazuli.UI;

public enum UpdateOutcome
{
    /// <summary>Seguir abrindo o app na versão atual.</summary>
    Continue,
    /// <summary>Instalado: a versão nova já foi aberta, este processo deve encerrar.</summary>
    Restarting,
    /// <summary>Encerrar sem abrir nada (atualização obrigatória recusada ou instalada sem poder reabrir).</summary>
    Quit,
}

/// <summary>
/// Janela de atualização: oferece, baixa com progresso, instala e reinicia. Obrigatória = sem "Depois".
/// Estados: Offer → Downloading → Installing → (reinicia) · Error (tentar de novo ou seguir).
/// </summary>
public partial class UpdateWindow : Window
{
    private enum Stage { Offer, Downloading, Installing, Error }

    private readonly UpdateService _svc;
    private readonly UpdateInfo _info;
    private readonly TaskCompletionSource<UpdateOutcome> _done = new();
    private CancellationTokenSource? _cts;
    private Stage _stage = Stage.Offer;
    private bool _finished;
    private bool _installed; // arquivos já trocados (só faltou reabrir)

    public UpdateWindow(UpdateService svc, UpdateInfo info)
    {
        InitializeComponent();
        Perf.Apply(this, Program.EarlySettings);
        _svc = svc;
        _info = info;

        SubtitleText.Text = $"Lazuli {info.VersionText}  ·  you have {AppInfo.VersionText}";
        FootText.Text = FormatSize(info.Size) + (info.Released.Length > 0 ? $"  ·  {info.Released}" : "");
        NotesList.ItemsSource = info.Notes.Count > 0 ? info.Notes : ["Improvements and fixes."];

        PrimaryBtn.Click += async (_, _) => await OnPrimary();
        SecondaryBtn.Click += (_, _) => OnSecondary();
        CloseBtn.Click += (_, _) => OnSecondary();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) OnSecondary();
            else if (e.Key == Key.Enter && PrimaryBtn.IsEnabled && PrimaryBtn.IsVisible) await OnPrimary();
        };
        Closing += (_, e) =>
        {
            if (_finished) return;
            if (_stage == Stage.Installing) { e.Cancel = true; return; } // não dá para interromper a troca de arquivos
            _cts?.Cancel();
            Finish(_installed || info.Mandatory ? UpdateOutcome.Quit : UpdateOutcome.Continue, close: false);
        };
        SetStage(Stage.Offer);
    }

    /// <summary>Abre a janela e espera o desfecho.</summary>
    public Task<UpdateOutcome> RunAsync()
    {
        Show();
        Activate();
        return _done.Task;
    }

    // ---------- estados ----------

    private void SetStage(Stage s, string? error = null)
    {
        _stage = s;
        var busy = s is Stage.Downloading or Stage.Installing;
        ProgressBox.IsVisible = busy;
        NotesCard.IsVisible = !busy;
        ErrorText.IsVisible = s == Stage.Error;
        ErrorText.Text = error ?? "";
        FootText.IsVisible = s != Stage.Error; // com botões largos no erro, o texto do rodapé só atrapalha
        CloseBtn.IsEnabled = s != Stage.Installing;
        PrimaryBtn.IsEnabled = !busy;
        SecondaryBtn.IsEnabled = s != Stage.Installing;

        switch (s)
        {
            case Stage.Offer:
                TitleText.Text = _info.Mandatory ? "Update required" : "New version available";
                PrimaryBtn.Content = "Update now";
                SecondaryBtn.Content = _info.Mandatory ? "Quit" : "Later";
                break;
            case Stage.Downloading:
                TitleText.Text = "Downloading update";
                SecondaryBtn.Content = "Cancel";
                break;
            case Stage.Installing:
                TitleText.Text = "Installing";
                SecondaryBtn.Content = "Cancel";
                break;
            case Stage.Error:
                TitleText.Text = "Update failed";
                PrimaryBtn.Content = "Try again";
                SecondaryBtn.Content = _info.Mandatory ? "Quit" : "Continue without updating";
                break;
        }
    }

    private async Task OnPrimary()
    {
        if (_stage is not (Stage.Offer or Stage.Error)) return;

        if (!UpdateService.CanWriteToAppDir())
        {
            SetStage(Stage.Error, "No permission to write to the app folder. Run as administrator or move it to your own folder.");
            return;
        }

        _cts = new CancellationTokenSource();
        SetStage(Stage.Downloading);
        SetProgress("Conectando…", 0, indeterminate: true);
        var clock = Stopwatch.StartNew();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            if (_stage != Stage.Downloading) return;
            var pct = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
            var speed = clock.Elapsed.TotalSeconds > 0.4 ? p.Done / clock.Elapsed.TotalSeconds : 0;
            SetProgress($"{FormatSize(p.Done)} de {FormatSize(p.Total)}" + (speed > 0 ? $"  ·  {FormatSize((long)speed)}/s" : ""), pct, false);
        });

        string zip;
        try
        {
            zip = await _svc.DownloadAsync(_info, progress, _cts.Token);
        }
        catch (OperationCanceledException) { PrimaryBtn.IsEnabled = !_finished; return; } // cancelado: o botão volta só depois da limpeza do download
        catch (UpdateException ex) { SetStage(Stage.Error, ex.Message); return; }
        catch (Exception ex) { SetStage(Stage.Error, "Unexpected download error: " + ex.Message); return; }
        if (_finished) return; // a janela foi fechada durante o download

        SetStage(Stage.Installing);
        SetProgress("Verifying and installing…", 100, indeterminate: true);
        try
        {
            await Task.Run(() => UpdateService.Install(zip));
        }
        catch (UpdateException ex) { SetStage(Stage.Error, ex.Message + " Nothing was changed."); return; }
        catch (Exception ex) { SetStage(Stage.Error, "Unexpected install error: " + ex.Message + " Nothing was changed."); return; }

        SetProgress("Restarting…", 100, indeterminate: false);
        await Task.Delay(350); // dá tempo de ver o 100%
        if (UpdateService.StartNewVersion()) { Finish(UpdateOutcome.Restarting); return; }

        _installed = true;
        SetStage(Stage.Error, "Updated! Couldn't relaunch: please reopen Lazuli.");
        TitleText.Text = "Update installed";
        PrimaryBtn.IsVisible = false;
        SecondaryBtn.Content = "Close";
    }

    private void OnSecondary()
    {
        if (_stage == Stage.Installing) return;
        if (_installed) { Finish(UpdateOutcome.Quit); return; }
        if (_stage == Stage.Downloading)
        {
            _cts?.Cancel();
            SetStage(Stage.Offer);
            PrimaryBtn.IsEnabled = false; // reabilita quando o download cancelado terminar de limpar
            return;
        }
        Finish(_info.Mandatory ? UpdateOutcome.Quit : UpdateOutcome.Continue);
    }

    private void Finish(UpdateOutcome outcome, bool close = true)
    {
        if (_finished) return;
        _finished = true;
        _done.TrySetResult(outcome);
        if (close) Close();
    }

#if DEBUG
    /// <summary>Só desenvolvimento: dispara a atualização sem clique (teste automatizado).</summary>
    internal Task StartForTest() => OnPrimary();

    /// <summary>Só desenvolvimento: mostra um estado para conferir o visual (captura de tela).</summary>
    internal void Preview(string view)
    {
        if (view == "updateprog")
        {
            SetStage(Stage.Downloading);
            SetProgress("27,4 MB de 52,9 MB  ·  6,1 MB/s", 52, false);
        }
        else if (view == "updateerr") SetStage(Stage.Error, "Downloaded file is corrupted (hash mismatch). Nothing was installed.");
    }
#endif

    // ---------- util ----------

    private void SetProgress(string status, double percent, bool indeterminate)
    {
        StatusText.Text = status;
        Bar.IsIndeterminate = indeterminate;
        Bar.Value = percent;
        PercentText.Text = indeterminate ? "" : $"{percent:0}%";
    }

    private static string FormatSize(long bytes) =>
        (bytes >= 1048576 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB").Replace('.', ',');
}
