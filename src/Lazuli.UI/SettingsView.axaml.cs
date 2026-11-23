using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Lazuli.UI.Services;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace Lazuli.UI;

/// <summary>Configurações dentro da janela principal (cobre a área de trabalho). Abre/fecha por MainViewModel.SettingsOpen.</summary>
public partial class SettingsView : UserControl
{
    private ViewModels.MainViewModel? Vm => DataContext as ViewModels.MainViewModel;
    private void CloseView() { if (Vm is { } m) m.SettingsOpen = false; }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is true)
        {
            MemoryText.Text = $"{System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB";
            Motion.FadeIn(this, 130);
            if (!IsSimple) Avalonia.Threading.Dispatcher.UIThread.Post(() => SearchBox.Focus(), Avalonia.Threading.DispatcherPriority.Background);
            else Focus();
        }
    }

    public SettingsView()
    {
        InitializeComponent();
        DoneBtn.Click += (_, _) => CloseView();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = ""; // primeiro Esc limpa a busca, o segundo fecha
                else CloseView();
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && !IsSimple) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        };

        // diagnóstico na página Sobre: como o app está renderizando e quanta memória usa agora
        BackendText.Text = Updates.AppInfo.BackendName;
        DataContextChanged += (_, _) =>
        {
            FillBackendChoices();
            if (DataContext is ViewModels.MainViewModel m) { WireLayout(m); WireAppearance(m); ApplyLayoutMode(m.IsSimple); }
        };
        Focusable = true;
        RenderText.Text = Program.UsingSoftware ? "Software (lite mode)" : "GPU (accelerated)";
        MemoryText.Text = $"{System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB";

        CheckNowBtn.Click += async (_, _) =>
        {
            CheckNowBtn.IsEnabled = false;
            CheckNowText.Text = "Checking…";
            CheckNowText.Text = await App.CheckNowAsync();
            CheckNowBtn.IsEnabled = true;
        };

        _pages = [PgAppearance, PgInterface, PgEditor, PgLazuli, PgAbout];
        Nav.SelectionChanged += (_, _) => { if (Nav.SelectedIndex >= 0) ShowTab(animate: true); };
        SearchBox.TextChanged += (_, _) => ApplyFilter(SearchBox.Text ?? "");
        ShowTab(animate: false);
    }

    // ---------- Abas e busca ----------

    private Control[] _pages = [];

    private bool IsSimple => Classes.Contains("simple");

    /// <summary>Layout simples: sem abas nem busca, uma lista curta com as opções marcadas "core". O layout só muda reiniciando.</summary>
    private void ApplyLayoutMode(bool simple)
    {
        Classes.Set("simple", simple);
        // simples: sem fundo próprio, aparece o mesmo fundo do editor (cor ou imagem); o editor some enquanto isto está aberto
        if (simple) Background = Brushes.Transparent;
        // temas: "Theme" à esquerda e bolinhas da cor de destaque à direita, como as outras linhas;
        // a escolhida ganha um anel. Sem caixas nem nomes embaixo.
        ThemeLabel.IsVisible = simple;
        Grid.SetColumn(ThemeSwatches, simple ? 1 : 0);
        Grid.SetColumnSpan(ThemeSwatches, simple ? 1 : 2);
        foreach (var (name, tile) in _tiles)
        {
            tile.Width = tile.Height = simple ? 24 : 0;
            if (!simple) { tile.Width = 64; tile.Height = 40; }
            tile.CornerRadius = new Avalonia.CornerRadius(simple ? 12 : 8);
            if (simple) tile.Background = Brushes.Transparent;
            if (tile.Child is Avalonia.Controls.Shapes.Ellipse dot) dot.Width = dot.Height = 14;
            if (tile.Parent is StackPanel sp && sp.Parent is Button b)
            {
                foreach (var t in sp.Children.OfType<TextBlock>()) t.IsVisible = !simple;
                b.Padding = new Avalonia.Thickness(simple ? 1 : 5);
                b.CornerRadius = new Avalonia.CornerRadius(simple ? 14 : 4);
                // "Custom" fica um pouco separado dos temas prontos
                b.Margin = simple ? new Avalonia.Thickness(name == ThemeManager.Custom ? 10 : 0, 0, 2, 0) : new Avalonia.Thickness(0, 0, 6, 6);
            }
        }
        _refreshAppearance?.Invoke();
        TopBar.IsVisible = !simple;
        ResetBtn.IsVisible = !simple;
        DoneBtn.Content = simple ? "Done" : "Back to editor";
        Body.Margin = simple ? new Avalonia.Thickness(22, 8, 22, 8) : new Avalonia.Thickness(24, 18, 24, 8);
        Body.MaxWidth = simple ? 620 : 780;
        FooterGrid.Height = simple ? 50 : 58;
        FooterGrid.Margin = simple ? new Avalonia.Thickness(22, 0) : new Avalonia.Thickness(24, 0);
        ApplyFilter(SearchBox.Text ?? "");
    }

    /// <summary>Mostra a aba escolhida (limpando a busca, se houver).</summary>
    private void ShowTab(bool animate)
    {
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
        ApplyFilter("");
        if (animate && Nav.SelectedIndex >= 0 && Nav.SelectedIndex < _pages.Length) Motion.FadeIn(_pages[Nav.SelectedIndex], 130);
    }

    /// <summary>Com texto na busca, mostra as opções que combinam de todas as abas; sem texto, só a aba escolhida.</summary>
    private void ApplyFilter(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).ToArray();
        var simple = IsSimple;
        if (simple) tokens = [];
        var searching = tokens.Length > 0;
        var flat = searching || simple; // mostra todas as páginas de uma vez, só os grupos com algo visível
        var any = false;
        for (var i = 0; i < _pages.Length; i++)
        {
            var tab = (Nav.Items.OfType<ListBoxItem>().ElementAt(i).Content?.ToString() ?? "").ToLowerInvariant();
            var pageShown = 0;
            foreach (var group in _pages[i].GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("group")))
            {
                var head = (group.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("section"))?.Text ?? "").ToLowerInvariant();
                var shown = 0;
                foreach (var row in group.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("srow")))
                {
                    var hay = $"{tab} {head} {row.Tag}";
                    var ok = tokens.All(t => hay.Contains(t, StringComparison.Ordinal)) && (!simple || row.Classes.Contains("core"));
                    row.IsVisible = ok;
                    if (ok) shown++;
                }
                group.IsVisible = !flat || shown > 0;
                pageShown += shown;
            }
            _pages[i].IsVisible = flat ? pageShown > 0 : i == Math.Max(0, Nav.SelectedIndex);
            any |= pageShown > 0;
        }
        NoResults.IsVisible = searching && !any;
        Scroll.Offset = default;
    }

    // ---------- Appearance ----------

    private bool _appearanceWired;
    private readonly List<(string Name, Border Tile)> _tiles = new();
    private bool _syncingColors;
    private Action? _refreshAppearance;

    private void WireAppearance(ViewModels.MainViewModel vm)
    {
        if (_appearanceWired) return;
        _appearanceWired = true;
        var st = vm.Settings;

        foreach (var p in ThemeManager.Presets) AddSwatch(vm, p.Name, p.Background, p.Accent);
        AddSwatch(vm, ThemeManager.Custom, null, null);

        void Refresh()
        {
            var accent = (IBrush)this.FindResource("Accent")!;
            var line = (IBrush)this.FindResource("LineStrong")!;
            // simples: anel claro só na escolhida; detalhado: borda de destaque na escolhida, linha nas outras
            var ring = IsSimple ? (IBrush)this.FindResource("Text")! : accent;
            var off = IsSimple ? Brushes.Transparent : line;
            foreach (var (name, tile) in _tiles) tile.BorderBrush = name == st.ThemeName ? ring : off;
            CustomPanel.IsVisible = st.ThemeName == ThemeManager.Custom;
            var (bg, ac) = ThemeManager.Resolve(st);
            if (_tiles.FirstOrDefault(t => t.Name == ThemeManager.Custom).Tile is { } ct)
            {
                if (!IsSimple) ct.Background = new SolidColorBrush(bg);
                if (ct.Child is Avalonia.Controls.Shapes.Ellipse dot) dot.Fill = new SolidColorBrush(ac);
            }
            ImageNote.Text = st.HasBackgroundImage ? "Image set. Use the sliders to balance beauty and readability." : "PNG, JPG, BMP or WebP, up to 25 MB.";
        }
        _refreshAppearance = Refresh;

        _syncingColors = true;
        CustomBgBox.Text = st.CustomBackground; CustomAccentBox.Text = st.CustomAccent;
        UpdateChip(CustomBgBox, CustomBgChip); UpdateChip(CustomAccentBox, CustomAccentChip);
        _syncingColors = false;

        CustomBgBox.TextChanged += (_, _) =>
        {
            UpdateChip(CustomBgBox, CustomBgChip);
            if (!_syncingColors && ThemeManager.TryParse(CustomBgBox.Text) is { } c) st.CustomBackground = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        };
        CustomAccentBox.TextChanged += (_, _) =>
        {
            UpdateChip(CustomAccentBox, CustomAccentChip);
            if (!_syncingColors && ThemeManager.TryParse(CustomAccentBox.Text) is { } c) st.CustomAccent = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        };

        // seletor embutido: clicar no quadradinho abre o painel para aquela cor; clicar de novo fecha
        TextBox? padTarget = null;
        var fromPad = false;
        void OpenPad(TextBox box, Border chip)
        {
            var line = (IBrush)this.FindResource("LineStrong")!;
            CustomBgChip.BorderBrush = line; CustomAccentChip.BorderBrush = line;
            if (padTarget == box && Pad.IsVisible) { Pad.IsVisible = false; padTarget = null; return; }
            padTarget = box;
            chip.BorderBrush = (IBrush)this.FindResource("Text")!;
            Pad.SetColor(ThemeManager.TryParse(box.Text) ?? Colors.Gray);
            Pad.IsVisible = true;
        }
        CustomBgChip.PointerPressed += (_, e) => { OpenPad(CustomBgBox, CustomBgChip); e.Handled = true; };
        CustomAccentChip.PointerPressed += (_, e) => { OpenPad(CustomAccentBox, CustomAccentChip); e.Handled = true; };
        Pad.ColorChanged += c =>
        {
            if (padTarget is null) return;
            fromPad = true;
            padTarget.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            fromPad = false;
        };
        // digitar o hex com o painel aberto move os marcadores junto
        foreach (var box in new[] { CustomBgBox, CustomAccentBox })
            box.TextChanged += (_, _) => { if (!fromPad && padTarget == box && ThemeManager.TryParse(box.Text) is { } c) Pad.SetColor(c); };

        ChooseImageBtn.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose a background image",
                AllowMultiple = false,
                FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] }],
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path is null) { ImageNote.Text = "Couldn't open that image."; return; }
            var name = ThemeManager.ImportImage(path, out var error);
            if (name is null) { ImageNote.Text = error ?? "Couldn't open that image."; return; }
            st.BackgroundImage = name;
            ThemeManager.CleanOldImages(name);
        };
        RemoveImageBtn.Click += (_, _) => { st.BackgroundImage = null; ThemeManager.CleanOldImages(null); };

        st.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    private void UpdateChip(TextBox box, Border chip)
    {
        var c = ThemeManager.TryParse(box.Text);
        chip.Background = c is { } v ? new SolidColorBrush(v) : Brushes.Transparent;
        box.Foreground = c is null ? new SolidColorBrush(Color.Parse("#E8837B")) : (IBrush)this.FindResource("Text")!;
    }

    private void AddSwatch(ViewModels.MainViewModel vm, string name, string? bg, string? accent)
    {
        var tile = new Border
        {
            Width = 64, Height = 40, CornerRadius = new Avalonia.CornerRadius(8), BorderThickness = new Avalonia.Thickness(2),
            Background = new SolidColorBrush(Color.Parse(bg ?? "#131315")),
            Child = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 14, Height = 14, Fill = new SolidColorBrush(Color.Parse(accent ?? "#6C8CFF")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
        var label = new TextBlock
        {
            Text = name, FontSize = 11.5, Classes = { "muted" }, Margin = new Avalonia.Thickness(0, 6, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        var btn = new Button
        {
            Classes = { "ghost" }, Padding = new Avalonia.Thickness(5), Margin = new Avalonia.Thickness(0, 0, 6, 6),
            Content = new StackPanel { Children = { tile, label } },
        };
        btn.Click += (_, _) => vm.Settings.ThemeName = name;
        _tiles.Add((name, tile));
        ThemeSwatches.Children.Add(btn);
    }

    private bool _layoutWired;

    /// <summary>Trocar o layout pede confirmação e reinicia o app (o layout é fixo durante a execução).</summary>
    private void WireLayout(ViewModels.MainViewModel vm)
    {
        if (_layoutWired) return;
        _layoutWired = true;
        void Sync() { SimpleRadio.IsChecked = vm.Settings.SimpleLayout; DetailedRadio.IsChecked = !vm.Settings.SimpleLayout; }
        Sync();
        async void Pick(bool simple)
        {
            if (simple == vm.Settings.SimpleLayout) return;
            var ok = await ConfirmRestart(simple ? "Simple" : "Detailed");
            if (!ok) { Sync(); return; }
            vm.Settings.SimpleLayout = simple;
            vm.FlushSettings();
            vm.SaveSession();
            if (!Updates.UpdateService.StartNewVersion(verify: false)) { vm.ShowToast("Couldn't restart: reopen the app manually"); return; }
            // fecha na hora: some com as janelas primeiro e encerra o processo sem esperar nada
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
            {
                foreach (var w in d.Windows.ToList()) w.Hide();
                Environment.Exit(0);
            }
        }
        SimpleRadio.Click += (_, _) => Pick(true);
        DetailedRadio.Click += (_, _) => Pick(false);
    }

    private Task<bool> ConfirmRestart(string layout)
    {
        var dlg = new Window
        {
            Width = 360, SizeToContent = SizeToContent.Height, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = "Lazuli",
            Background = (Avalonia.Media.IBrush)this.FindResource("ChromeSolid")!,
        };
        var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };
        var restart = new Button { Content = "Restart", Classes = { "secondary", "default" } };
        cancel.Click += (_, _) => dlg.Close(false);
        restart.Click += (_, _) => dlg.Close(true);
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22), Spacing = 6,
            Children =
            {
                new TextBlock { Text = $"Switch to {layout} layout?", FontSize = 15, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = "Lazuli needs to restart to change the layout. Your tabs are saved.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Margin = new Avalonia.Thickness(0, 18, 0, 0), Children = { cancel, restart } },
            },
        };
        return TopLevel.GetTopLevel(this) is Window owner ? dlg.ShowDialog<bool>(owner) : Task.FromResult(false);
    }

    private bool _backendWired;

    public void ShowPage(int index) { if (index >= 0 && index < _pages.Length) Nav.SelectedIndex = index; }

    /// <summary>Opções do seletor de backend: Automático, Demonstração e cada pasta válida de backends\. A escolha fica em Settings.BackendId.</summary>
    private void FillBackendChoices()
    {
        if (_backendWired || DataContext is not ViewModels.MainViewModel vm) return;
        _backendWired = true;
        var current = vm.Settings.BackendId;
        var items = new List<(string? Id, string Text)> { (null, "Auto"), (Contracts.BackendInfo.DemoId, "Demo") };
        items.AddRange(Backends.BackendHost.Discovery.Backends.Select(e => ((string?)e.Manifest.Id, e.Manifest.DisplayName)));
        if (current is not null && items.All(i => i.Id != current)) items.Add((current, $"{current} (not found)"));

        BackendCombo.ItemsSource = items.Select(i => new ComboBoxItem { Content = i.Text, Tag = i.Id }).ToList();
        BackendCombo.SelectedIndex = items.FindIndex(i => i.Id == current);
        BackendCombo.SelectionChanged += (_, _) =>
        {
            if (BackendCombo.SelectedItem is ComboBoxItem { } it) vm.Settings.BackendId = it.Tag as string;
        };
    }
}
