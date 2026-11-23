using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Executor.UI.Services;
using Avalonia.Input;

namespace Executor.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Motion.WindowOpen(this);
        CloseBtn.Click += (_, _) => Close();
        DoneBtn.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // diagnóstico na página Sobre: como o app está renderizando e quanta memória usa agora
        BackendText.Text = Updates.AppInfo.BackendName;
        DataContextChanged += (_, _) => { FillBackendChoices(); if (DataContext is ViewModels.MainViewModel m) { WireLayout(m); WireAppearance(m); } };
        RenderText.Text = Program.UsingSoftware ? "Software (lite mode)" : "GPU (accelerated)";
        MemoryText.Text = $"{System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB";

        Control[] pages = [PageEditor, PageInterface, PageAppearance, PageSession, PageAbout];
        Nav.SelectionChanged += (_, _) =>
        {
            for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == Nav.SelectedIndex;
            if (Nav.SelectedIndex >= 0 && Nav.SelectedIndex < pages.Length) Motion.FadeIn(pages[Nav.SelectedIndex], 130);
        };
    }

    // ---------- Appearance ----------

    private bool _appearanceWired;
    private readonly List<(string Name, Border Tile)> _tiles = new();
    private bool _syncingColors;

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
            foreach (var (name, tile) in _tiles) tile.BorderBrush = name == st.ThemeName ? accent : line;
            CustomPanel.IsVisible = st.ThemeName == ThemeManager.Custom;
            var (bg, ac) = ThemeManager.Resolve(st);
            if (_tiles.FirstOrDefault(t => t.Name == ThemeManager.Custom).Tile is { } ct)
            {
                ct.Background = new SolidColorBrush(bg);
                if (ct.Child is Avalonia.Controls.Shapes.Ellipse dot) dot.Fill = new SolidColorBrush(ac);
            }
            ImageNote.Text = st.HasBackgroundImage ? "Image set. Use the sliders to balance beauty and readability." : "PNG, JPG, BMP or WebP, up to 25 MB.";
        }

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

        ChooseImageBtn.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
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
        return dlg.ShowDialog<bool>(this);
    }

    private bool _backendWired;

    public void ShowPage(int index) => Nav.SelectedIndex = index;

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
