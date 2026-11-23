using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lazuli.Contracts;
using Lazuli.UI.Backends;
using Lazuli.UI.Mocks;
using Lazuli.UI.Services;
using Lazuli.UI.Updates;
using Lazuli.UI.ViewModels;

namespace Lazuli.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // erro numa ação da tela: registra e segue (um clique com problema não deve derrubar o app inteiro)
        Dispatcher.UIThread.UnhandledException += (_, e) => { CrashLog.Write("interface", e.Exception); e.Handled = true; };
        ThemeManager.Attach(Program.EarlySettings); // cores e imagem de fundo escolhidas, antes de qualquer janela aparecer
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if DEBUG
            // autoteste da lógica (sem janela)
            if (Environment.GetEnvironmentVariable("LAZULI_SELFTEST") is { Length: > 0 } testDir)
            {
                _ = Task.Run(async () =>
                {
                    try { Environment.ExitCode = await SelfTest.RunAsync(testDir); }
                    catch (Exception ex) { Console.WriteLine("ERRO NO AUTOTESTE: " + ex); Environment.ExitCode = 2; }
                    Dispatcher.UIThread.Post(() => desktop.Shutdown());
                });
                base.OnFrameworkInitializationCompleted();
                return;
            }
            // teste de integração do editor: digita no editor real, fora da tela
            if (Environment.GetEnvironmentVariable("LAZULI_EDITTEST") == "1")
            {
                var vmT = new MainViewModel(new MockScriptLibrary(), new MockRunSession(), new AppSettings { RestoreTabs = false })
                    { PersistenceEnabled = false };
                var wT = new MainWindow { DataContext = vmT };
                desktop.MainWindow = wT;
                wT.WindowStartupLocation = WindowStartupLocation.Manual;
                wT.Position = new PixelPoint(-30000, -30000);
                wT.ShowActivated = false;
                wT.ShowInTaskbar = false;
                wT.Opened += async (_, _) =>
                {
                    try { await EditTest.RunAsync(wT); }
                    catch (Exception ex) { Console.WriteLine("ERRO NO TESTE DO EDITOR: " + ex); }
                    Dispatcher.UIThread.Post(() => desktop.Shutdown());
                };
                base.OnFrameworkInitializationCompleted();
                return;
            }
#endif
#if DEBUG
            var shot = Environment.GetEnvironmentVariable("LAZULI_SHOT");
            // no modo captura usa configurações padrão e não toca nos arquivos do usuário
            var settings = string.IsNullOrEmpty(shot) ? Program.EarlySettings : new AppSettings { RestoreTabs = false };
            // LAZULI_SHOT_GEOM=1: simula uma janela salva (820x520 na posição 100,100) para testar a restauração
            if (!string.IsNullOrEmpty(shot) && Environment.GetEnvironmentVariable("LAZULI_SHOT_LITE") == "1")
                settings.PerformanceMode = "Lite";
            if (!string.IsNullOrEmpty(shot) && Environment.GetEnvironmentVariable("LAZULI_SHOT_GEOM") == "1")
                settings.CopyWindowFrom(new AppSettings { WindowWidth = 820, WindowHeight = 520, WindowX = 100, WindowY = 100, HasWindowPos = true });
#else
            var settings = Program.EarlySettings;
#endif
            // Biblioteca = pasta de verdade (%APPDATA%\Lazuli\Scripts). Troque MockRunSession por um backend real aqui.
            IScriptLibrary library = new FolderScriptLibrary();
#if DEBUG
            // captura de tela: dados fixos, sem criar nem ler arquivos do usuário
            if (!string.IsNullOrEmpty(shot)) library = new MockScriptLibrary();
#endif
            // Backend: escolhido uma vez, aqui, pelas configurações (BackendId) e pelo que existe em backends\. Qualquer falha cai na demonstração.
            var requestedBackend = settings.BackendId;
#if DEBUG
            if (!string.IsNullOrEmpty(shot)) requestedBackend = BackendInfo.DemoId; // captura de tela: nunca carrega plugin
#endif
            var backend = BackendHost.Resolve(requestedBackend, BackendCatalog.DefaultRoot, AppInfo.Version, Storage.BackendDataRoot);
            var session = backend.Session;
            MainWindow BuildMain() =>
                new() { DataContext = CreateVm() };
            MainViewModel CreateVm()
            {
                var vm = new MainViewModel(library, session, settings) { Backend = backend.Info };
                foreach (var w in backend.Warnings) vm.Log(LogLevel.Warning, w);
                vm.StartTargetWatch();
                vm.StartHome();
                return vm;
            }
#if DEBUG
            if (!string.IsNullOrEmpty(shot))
            {
                // captura de tela: sem splash
                var window = BuildMain();
                desktop.MainWindow = window;
                SetupOffscreenShot(desktop, window, (MainViewModel)window.DataContext!, shot);
                base.OnFrameworkInitializationCompleted();
                return;
            }
#endif
            // LAZULI_NOSPLASH=1: abre direto (usado para medir o tempo real de abertura)
            if (settings.ShowSplash && Environment.GetEnvironmentVariable("LAZULI_NOSPLASH") != "1") ShowWithSplash(desktop, BuildMain);
            else
            {
                desktop.MainWindow = BuildMain();
                if (UpdateConfig.Enabled) // sem splash: procura depois de abrir e oferece por cima
                    desktop.MainWindow.Opened += (_, _) => _ = OfferWhenReadyAsync(desktop, CheckForUpdateAsync());
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Abre a splash primeiro; a janela principal é montada por trás e entra quando fica pronta
    /// (a splash fica pelo menos ~1 s para não piscar), então a splash some com fade.
    /// </summary>
    private static void ShowWithSplash(IClassicDesktopStyleApplicationLifetime desktop, Func<MainWindow> build)
    {
        var splash = new SplashWindow();
        desktop.MainWindow = splash;
        splash.Opened += async (_, _) =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(120); // deixa a splash desenhar antes do trabalho pesado

            // procura atualização em paralelo com a montagem da janela principal (não atrasa a abertura)
            Task<UpdateInfo?>? check = null;
            if (UpdateConfig.Enabled)
            {
                splash.SetStatus("Checking for updates…");
                check = CheckForUpdateAsync();
            }
            var main = build();
            var wait = 700 - (int)clock.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait);

            // dá até ~2,5 s para a resposta chegar antes de abrir; se demorar (rede lenta), o app abre e a oferta aparece depois
            var late = false;
            if (check is not null)
            {
                var grace = Math.Max(0, 2500 - (int)clock.ElapsedMilliseconds);
                if (await Task.WhenAny(check, Task.Delay(grace)) == check)
                {
                    if (check.Result is { } info)
                    {
                        splash.Hide(); // a janela de atualização fica sozinha; a splash some quando o app abrir
                        if (await RunUpdateWindowAsync(info) is UpdateOutcome.Restarting or UpdateOutcome.Quit)
                        {
                            desktop.Shutdown();
                            return;
                        }
                    }
                }
                else late = true;
            }
            splash.SetStatus("Starting…");
            desktop.MainWindow = main;
            main.Show();
            if (late) _ = OfferWhenReadyAsync(desktop, check!);
            await Motion.FadeOutAsync(splash, 220);
            splash.Close();
        };
    }

    private static readonly Lazy<UpdateService> UpdateSvc = new(() => new UpdateService());

    /// <summary>Procura atualização sem nunca atrapalhar a abertura: qualquer falha (sem internet, servidor fora) vira "nada novo".</summary>
    private static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try { return await UpdateSvc.Value.CheckAsync(CancellationToken.None); }
        catch (Exception ex) { Console.Error.WriteLine("[update] " + ex.Message); return null; }
    }

    /// <summary>Espera a verificação e, se houver novidade, abre a janela de atualização por cima do app.</summary>
    private static async Task OfferWhenReadyAsync(IClassicDesktopStyleApplicationLifetime desktop, Task<UpdateInfo?> check)
    {
        var info = await check;
        if (info is not null && await RunUpdateWindowAsync(info) is UpdateOutcome.Restarting or UpdateOutcome.Quit)
            desktop.Shutdown();
    }

    private static Task<UpdateOutcome> RunUpdateWindowAsync(UpdateInfo info)
    {
        var win = new UpdateWindow(UpdateSvc.Value, info);
#if DEBUG
        // teste automatizado: aceita sozinho (só existe em build de desenvolvimento)
        if (Environment.GetEnvironmentVariable("LAZULI_UPDATE_AUTO") == "1")
            win.Opened += async (_, _) => await win.StartForTest();
#endif
        return win.RunAsync();
    }

#if DEBUG
    // Só para desenvolvimento: renderiza fora da tela, salva PNG e fecha. Não rouba foco nem salva nada.
    private static void SetupOffscreenShot(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel vm, string path)
    {
        vm.PersistenceEnabled = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-30000, -30000);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Opened += async (_, _) =>
        {
            vm.EnsureLibrary();
            vm.OpenScriptCommand.Execute(vm.FilteredLibrary[2]);
            vm.OpenScriptCommand.Execute(vm.FilteredLibrary[1]);

            // LAZULI_SHOT_VIEW = lista separada por vírgula: attach, open, tabs12, dirty, palette, menu, confirm,
            // drop, toast, search, rename, settingsN. Só para conferir o visual de cada estado.
            var views = (Environment.GetEnvironmentVariable("LAZULI_SHOT_VIEW") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            bool Has(string v) => views.Contains(v);
            if (Has("tabs12")) for (var i = 0; i < 12; i++) vm.NewTabCommand.Execute(null);
            if (Has("attach")) await vm.AttachCommand.ExecuteAsync(null);
            if (Has("open")) { vm.LibraryOpen = true; vm.ConsoleOpen = true; }
            if (Has("clients"))
            {
                vm.ClientsOpen = true;
                await vm.RefreshClientsNowCommand.ExecuteAsync(null);
                if (vm.Clients.Count > 0) vm.Clients[0].IsSelected = true;
            }
            if (Has("killarmed")) { _ = vm.KillAllCommand.ExecuteAsync(null); await Task.Delay(500); }
            if (Has("dirty") || Has("confirm")) { var t = vm.Tabs[1]; t.Content += "-- editado\n"; }
            await vm.RunCommand.ExecuteAsync(null);
            if (Has("palette")) vm.OpenPalette();
            if (Has("menu")) vm.OpenTabMenu(vm.Tabs[1], 170, 46);
            if (Has("confirm")) vm.CloseTabCommand.Execute(vm.Tabs[1]);
            if (Has("drop")) vm.DropHint = true;
            if (Has("toast")) vm.ShowToast("Salvo · loops.lua");
            if (Has("search")) window.OpenSearch();
            if (Has("rename")) vm.BeginRenameCommand.Execute(vm.Tabs[1]);
            if (Has("home")) { vm.HomeOpen = true; await vm.RefreshHomeCommand.ExecuteAsync(null); await Task.Delay(300); }

            Window target = window;
            if (Has("splash"))
            {
                var sp = new SplashWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Position = new PixelPoint(-30000, -30000), ShowActivated = false,
                };
                sp.Show();
                target = sp;
            }
            if (views.FirstOrDefault(v => v.StartsWith("update")) is { } uview)
            {
                var info = new UpdateInfo(new Version(0, 2, 0), uview == "updatemandatory",
                    ["Atualização automática com verificação na abertura", "Barra de status centralizada e hover sem flash", "Animações mais rápidas"],
                    new Uri("https://example.invalid/x.zip"), new string('a', 64), 52_900_000, "2026-11-21");
                var uw = new UpdateWindow(new UpdateService(), info)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Position = new PixelPoint(-30000, -30000), ShowActivated = false,
                };
                uw.Show();
                uw.Preview(uview);
                target = uw;
            }
            if (views.FirstOrDefault(v => v.StartsWith("settings")) is { } view)
            {
                var sw = new SettingsWindow
                {
                    DataContext = vm, WindowStartupLocation = WindowStartupLocation.Manual,
                    Position = new PixelPoint(-30000, -30000), ShowActivated = false,
                };
                sw.Show();
                if (int.TryParse(view.AsSpan("settings".Length), out var page)) sw.ShowPage(page);
                target = sw;
            }
            await Task.Delay(1200);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var size = new PixelSize((int)target.Bounds.Width, (int)target.Bounds.Height);
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(target);
                bmp.Save(path);
            });
            desktop.Shutdown();
        };
    }
#endif
}
