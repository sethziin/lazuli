using Avalonia.Controls;

namespace Lazuli.UI.Services;

/// <summary>
/// Modo de desempenho. "Leve" = renderização por software (inicia mais rápido e usa bem menos memória/threads,
/// ótimo em PC fraco ou GPU/driver ruim), sem animações nem sombras. "Automático" escolhe pelo hardware.
/// </summary>
public static class Perf
{
    public const string Auto = "Auto", Normal = "Normal", Lite = "Lite";

    /// <summary>PC "fraco": 6 GB de RAM ou menos, ou 2 núcleos ou menos.</summary>
    public static bool IsLowEndHardware()
    {
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return ram <= 6L * 1024 * 1024 * 1024 || Environment.ProcessorCount <= 2;
    }

    public static bool IsLite(AppSettings s) => s.PerformanceMode switch
    {
        Lite => true,
        Normal => false,
        _ => IsLowEndHardware(),
    };

    /// <summary>
    /// Liga/desliga os efeitos (animações, transições, sombras) numa janela: os estilos de efeito só valem em
    /// janelas com a classe "fx". Também ajusta o interruptor global das animações feitas por código.
    /// </summary>
    public static void Apply(Window window, AppSettings s)
    {
        var lite = IsLite(s);
        window.Classes.Set("fx", !lite);
        Motion.Enabled = !lite;
    }
}
