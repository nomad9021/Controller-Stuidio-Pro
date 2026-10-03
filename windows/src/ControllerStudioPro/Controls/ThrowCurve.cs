using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ControllerStudio;

namespace ControllerStudioPro.Controls;

/// <summary>How the trigger pull maps to what games receive (the engine's remap), with the live pull.</summary>
public sealed class ThrowCurve : FrameworkElement
{
    public static readonly DependencyProperty OutputProperty = DependencyProperty.Register(
        nameof(Output), typeof(TriggerOutput), typeof(ThrowCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnOutputChanged));
    public static readonly DependencyProperty PullProperty = DependencyProperty.Register(
        nameof(Pull), typeof(double), typeof(ThrowCurve),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ThrowCurve()
    {
        Width = 220;
        Height = 220;
    }

    public TriggerOutput? Output { get => (TriggerOutput?)GetValue(OutputProperty); set => SetValue(OutputProperty, value); }
    public double Pull { get => (double)GetValue(PullProperty); set => SetValue(PullProperty, value); }

    static void OnOutputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ThrowCurve)d;
        if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= c.Changed;
        if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += c.Changed;
    }

    void Changed(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var o = Output;
        if (o is null)
            return;
        const double L = 24, T = 6, S = 186;  // plot square
        double X(double x) => L + x * S;
        double Y(double y) => T + S - y * S;
        var grid = new Pen(Theme.Divider(this), 1);
        var shade = Theme.Fill(this);
        var accent = Theme.Accent(this);
        dc.DrawRectangle(Theme.WithOpacity(Theme.Fill(this), 0.5), null, new Rect(L, T, S, S));
        dc.DrawRectangle(shade, null, new Rect(L, T, o.Deadzone / 100.0 * S, S));
        double full = Math.Max(o.Deadzone + 5, o.FullAt) / 100.0;
        dc.DrawRectangle(shade, null, new Rect(X(full), T, S - full * S, S));
        dc.DrawLine(grid, new Point(L, T + S / 2), new Point(L + S, T + S / 2));
        dc.DrawLine(grid, new Point(L + S / 2, T), new Point(L + S / 2, T + S));

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(0), Y(Effects.RemapUnit(0, o))), false, false);
            for (int i = 1; i <= 100; i++)
                g.LineTo(new Point(X(i / 100.0), Y(Effects.RemapUnit(i / 100.0, o))), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 2.5) { LineJoin = PenLineJoin.Round }, geo);
        dc.DrawEllipse(accent, new Pen(Theme.Text(this), 1.5), new Point(X(Pull), Y(Effects.RemapUnit(Pull, o))), 5.5, 5.5);

        var muted = Theme.TextSecondary(this);
        var pull = Theme.Label(this, "Your pull", 11, muted);
        dc.DrawText(pull, new Point(L + (S - pull.Width) / 2, T + S + 4));
        var sees = Theme.Label(this, "Game sees", 11, muted);
        dc.PushTransform(new RotateTransform(-90, 0, 0));
        dc.DrawText(sees, new Point(-(T + S / 2 + sees.Width / 2), 4));
        dc.Pop();
    }
}
