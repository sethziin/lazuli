using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Executor.UI;

/// <summary>
/// Bolinha de status no ícone da barra de tarefas, do jeito que Discord/Signal/Electron fazem:
/// ITaskbarList3.SetOverlayIcon com um ícone 32 bits com transparência (o Windows posiciona e escala o selo).
/// O ícone é um PNG convertido em HICON por CreateIconFromResourceEx, que preserva o canal alfa.
/// </summary>
internal static class TaskbarBadge
{
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint h); void DeleteTab(nint h); void ActivateTab(nint h); void SetActiveAlt(nint h);
        void MarkFullscreenWindow(nint h, [MarshalAs(UnmanagedType.Bool)] bool f);
        void SetProgressValue(nint h, ulong done, ulong total); void SetProgressState(nint h, int flags);
        void RegisterTab(nint tab, nint main); void UnregisterTab(nint tab);
        void SetTabOrder(nint tab, nint before); void SetTabActive(nint tab, nint main, uint r);
        void ThumbBarAddButtons(nint h, uint n, nint b); void ThumbBarUpdateButtons(nint h, uint n, nint b);
        void ThumbBarSetImageList(nint h, nint il);
        void SetOverlayIcon(nint h, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? desc);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090")] private class TaskbarList { }

    [DllImport("user32.dll")] private static extern nint CreateIconFromResourceEx(byte[] data, int size, bool icon, int ver, int cx, int cy, int flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint i);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint h);

    private static ITaskbarList3? _tb;
    private static nint _icon;
    private static string? _last;

    public static void Set(nint hwnd, string hexColor, string description)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0 || hexColor == _last) return;
        try
        {
            _tb ??= (ITaskbarList3)new TaskbarList();
            _tb.HrInit();
            var dpi = GetDpiForWindow(hwnd);
            var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi == 0 ? 96 : dpi); // 16 a 100%, 20 a 125%…
            var png = DotPng(size, Color.Parse(hexColor));
            var icon = CreateIconFromResourceEx(png, png.Length, true, 0x00030000, size, size, 0);
            if (icon == 0) return;
            _tb.SetOverlayIcon(hwnd, icon, description);
            if (_icon != 0) DestroyIcon(_icon);
            _icon = icon;
            _last = hexColor;
        }
        catch { /* só um enfeite: sem selo, nada quebra */ }
    }

    /// <summary>Bolinha igual à do app (miolo + anel escuro), ocupando o selo inteiro.</summary>
    private static byte[] DotPng(int size, Color color)
    {
        using var rt = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = rt.CreateDrawingContext())
        {
            // igual à bolinha do app: miolo colorido + anel escuro (cor do Chrome) em volta, encostada no canto
            var ring = Math.Max(2.5, size * 0.22);
            var r = size / 2.0;
            ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#131315")), null, new Point(r, r), r - 0.25, r - 0.25);
            ctx.DrawEllipse(new SolidColorBrush(color), null, new Point(r, r), r - ring, r - ring);
        }
        using var ms = new MemoryStream();
        rt.Save(ms);
        return ms.ToArray();
    }
}
