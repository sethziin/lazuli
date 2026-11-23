using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Lazuli.UI;

/// <summary>
/// Ícone de traço desenhado numa grade fixa de 24×24 (o padrão dos StreamGeometry Icon* do tema), escalado
/// por igual para o tamanho do controle. Diferente de um Path com Stretch, o desenho nunca encosta num canto:
/// ícones largos ou altos ficam centralizados, todos com a mesma espessura de traço.
/// A cor vem do Foreground herdado (ex.: o do botão), ou de <see cref="Brush"/> se definido.
/// </summary>
public sealed class Ico : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Ico, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<Ico, IBrush?>(nameof(Brush));

    /// <summary>Espessura do traço em pixels de tela (não na grade 24).</summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<Ico, double>(nameof(Thickness), 1.6);

    /// <summary>Preenche em vez de traçar (ex.: o triângulo de "Executar").</summary>
    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<Ico, bool>(nameof(Filled));

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    static Ico()
    {
        AffectsRender<Ico>(DataProperty, BrushProperty, ThicknessProperty, FilledProperty, TextElement.ForegroundProperty);
        WidthProperty.OverrideDefaultValue<Ico>(16);
        HeightProperty.OverrideDefaultValue<Ico>(16);
        VerticalAlignmentProperty.OverrideDefaultValue<Ico>(Avalonia.Layout.VerticalAlignment.Center);
        HorizontalAlignmentProperty.OverrideDefaultValue<Ico>(Avalonia.Layout.HorizontalAlignment.Center);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Foreground é herdado do botão (muda no hover/ativo): redesenha
        if (change.Property == TextElement.ForegroundProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null) return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var brush = Brush ?? GetValue(TextElement.ForegroundProperty) ?? Brushes.White;
        var scale = size / 24.0;
        var dx = (Bounds.Width - size) / 2;
        var dy = (Bounds.Height - size) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
        {
            if (Filled) context.DrawGeometry(brush, null, Data);
            else
            {
                var pen = new Pen(brush, Thickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                context.DrawGeometry(null, pen, Data);
            }
        }
    }
}
