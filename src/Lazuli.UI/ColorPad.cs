using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace Lazuli.UI;

/// <summary>
/// Seletor de cor embutido (sem pacote extra): área de saturação × brilho e barra de matiz.
/// Arrastar em qualquer uma das duas muda a cor; <see cref="ColorChanged"/> avisa a cada passo.
/// </summary>
public sealed class ColorPad : StackPanel
{
    private const double PadW = 220, PadH = 120, HueH = 12;

    private readonly Border _sv;
    private readonly Ellipse _svMark;
    private readonly Canvas _svCanvas;
    private readonly Border _hueMark;
    private readonly Canvas _hueCanvas;

    private double _h, _s, _v = 1;

    public event Action<Color>? ColorChanged;

    public ColorPad()
    {
        Spacing = 8;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;

        // área S×V: cor pura do matiz + branco da esquerda para a direita + preto de cima para baixo
        _sv = new Border { Width = PadW, Height = PadH, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        var white = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
            },
        };
        var black = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1) },
            },
        };
        _svMark = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        _svCanvas = new Canvas { Width = PadW, Height = PadH, Background = Brushes.Transparent, Children = { _svMark } };
        _sv.Child = new Panel { Children = { white, black, _svCanvas } };

        // barra de matiz
        var hueBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        for (var i = 0; i <= 6; i++) hueBrush.GradientStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        var hueBar = new Border { Width = PadW, Height = HueH, CornerRadius = new CornerRadius(6), Background = hueBrush };
        _hueMark = new Border
        {
            Width = 6, Height = HueH + 4, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
        };
        _hueCanvas = new Canvas { Width = PadW, Height = HueH + 4, Background = Brushes.Transparent, Children = { _hueMark } };
        var hue = new Panel { Width = PadW, Height = HueH + 4, Children = { new Panel { Margin = new Thickness(0, 2), Children = { hueBar } }, _hueCanvas } };

        Children.Add(_sv);
        Children.Add(hue);

        Track(_svCanvas, p =>
        {
            _s = Math.Clamp(p.X / PadW, 0, 1);
            _v = 1 - Math.Clamp(p.Y / PadH, 0, 1);
        });
        Track(_hueCanvas, p => _h = Math.Clamp(p.X / PadW, 0, 1) * 359.9);
        Update();
    }

    /// <summary>Posiciona os marcadores na cor dada (sem disparar <see cref="ColorChanged"/>).</summary>
    public void SetColor(Color c)
    {
        (_h, _s, _v) = ToHsv(c);
        Update();
    }

    private void Track(Control area, Action<Point> apply)
    {
        var dragging = false;
        void Handle(PointerEventArgs e)
        {
            apply(e.GetPosition(area));
            Update();
            ColorChanged?.Invoke(FromHsv(_h, _s, _v));
        }
        area.PointerPressed += (_, e) => { dragging = true; e.Pointer.Capture(area); Handle(e); e.Handled = true; };
        area.PointerMoved += (_, e) => { if (dragging) Handle(e); };
        area.PointerReleased += (_, e) => { dragging = false; e.Pointer.Capture(null); };
        area.PointerCaptureLost += (_, _) => dragging = false;
    }

    private void Update()
    {
        _sv.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        Canvas.SetLeft(_svMark, _s * PadW - _svMark.Width / 2);
        Canvas.SetTop(_svMark, (1 - _v) * PadH - _svMark.Height / 2);
        Canvas.SetLeft(_hueMark, _h / 360 * PadW - _hueMark.Width / 2);
        Canvas.SetTop(_hueMark, 0);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte B(double t) => (byte)Math.Round(Math.Clamp(t, 0, 1) * 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * ((g - b) / d % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
