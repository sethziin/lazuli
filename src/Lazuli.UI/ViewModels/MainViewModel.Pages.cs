using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lazuli.UI.ViewModels;

/// <summary>Script de auto-execução (placeholder).</summary>
public partial class AutoExecItem : ObservableObject
{
    public AutoExecItem(string name, bool on) { Name = name; _enabled = on; }
    public string Name { get; }
    [ObservableProperty] private bool _enabled;
}

/// <summary>Páginas extras do layout detalhado. O Script Hub está em MainViewModel.Hub.cs; Auto-execute ainda é só visual.</summary>
public partial class MainViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EditorActive))] private bool _hubOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EditorActive))] private bool _autoExecOpen;

    public ObservableCollection<AutoExecItem> AutoExecScripts { get; } =
    [
        new("anti-afk.lua", true),
        new("fps-unlocker.lua", false),
        new("chat-logger.lua", false),
    ];

    private void ClosePages() { HubOpen = false; AutoExecOpen = false; }

    [RelayCommand] private void ShowHub() { ClosePages(); HomeOpen = false; HubOpen = true; EnsureHubLoaded(); }
    [RelayCommand] private void ShowAutoExec() { ClosePages(); HomeOpen = false; AutoExecOpen = true; }

    [RelayCommand] private void Placeholder() => ShowToast("Coming soon");
}
