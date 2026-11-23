using Avalonia.Controls;
using Executor.UI.Services;

namespace Executor.UI;

/// <summary>Tela de abertura simples: nome, uma linha de progresso e fade. Some quando a janela principal fica pronta.</summary>
public partial class SplashWindow : Window
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint h, int attr, ref int value, int size);

    /// <summary>Cantos arredondados do Windows 11 (igual à janela principal); no Windows 10 não faz nada.</summary>
    private void RoundCorners()
    {
        try { if (TryGetPlatformHandle()?.Handle is { } h) { var round = 2; DwmSetWindowAttribute(h, 33, ref round, sizeof(int)); } }
        catch { }
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public SplashWindow()
    {
        InitializeComponent();
        RoundCorners();
        Opened += (_, _) => RoundCorners();
        Opened += (_, _) => Motion.WindowOpen(this);
        Perf.Apply(this, Program.EarlySettings);
        VersionText.Text = Updates.AppInfo.VersionText;
    }
}
