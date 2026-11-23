using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Executor.UI;

/// <summary>Animações curtas e discretas (fade) usadas ao abrir painéis e trocar páginas.</summary>
public static class Motion
{
    /// <summary>Desligado no modo leve: nenhuma animação por código roda.</summary>
    public static bool Enabled { get; set; } = true;

    public static void FadeIn(Control? c, int ms = 180)
    {
        if (c is null || !Enabled) return;
        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(ms),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
            },
        };
        _ = anim.RunAsync(c);
    }

    /// <summary>Abertura de janela: aparece com fade e cresce de 96% para 100%, como as janelas nativas do Windows.</summary>
    public static void WindowOpen(Window w, int ms = 220)
    {
        if (!Enabled || w.Content is not Control root) return;
        try
        {
            var d = TimeSpan.FromMilliseconds(ms);
            root.RenderTransformOrigin = RelativePoint.Center;
            root.Opacity = 0;
            root.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.96)");
            root.Transitions =
            [
                new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = d, Easing = new CubicEaseOut() },
                new Avalonia.Animation.TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = d, Easing = new CubicEaseOut() },
            ];
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                root.Opacity = 1;
                root.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch { root.Opacity = 1; root.RenderTransform = null; } // animação nunca pode travar a abertura
    }

    /// <summary>Some com fade e fica transparente (use antes de fechar a janela).</summary>
    public static async Task FadeOutAsync(Control? c, int ms = 200)
    {
        if (c is null || !Enabled) return;
        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(ms),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 0.0) } },
            },
        };
        await anim.RunAsync(c);
    }
}
