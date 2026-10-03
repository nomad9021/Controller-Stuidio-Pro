using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace ControllerStudioPro.Controls;

/// <summary>Ten resistance zones (0..8) down the trigger pull. Drag the bars, or use the arrow keys.</summary>
public sealed class ZonesEditor : FrameworkElement
{
    public static readonly DependencyProperty ZonesProperty = DependencyProperty.Register(
        nameof(Zones), typeof(List<int>), typeof(ZonesEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault |
                                            FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PullProperty = DependencyProperty.Register(
        nameof(Pull), typeof(double), typeof(ZonesEditor),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    const double Gap = 6, NumberSpace = 20;
    int _focus;

    public ZonesEditor()
    {
        Focusable = true;
        Height = 150;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        AutomationProperties.SetName(this, "Resistance zones. Left and right pick a zone, up and down change it.");
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    public List<int>? Zones { get => (List<int>?)GetValue(ZonesProperty); set => SetValue(ZonesProperty, value); }
    public double Pull { get => (double)GetValue(PullProperty); set => SetValue(PullProperty, value); }

    double ColumnWidth => (ActualWidth - Gap * 9) / 10;

    protected override void OnRender(DrawingContext dc)
    {
        var zones = Zones ?? [];
        double w = ColumnWidth, h = ActualHeight - NumberSpace;
        if (w <= 0 || h <= 0)
            return;
        var track = Theme.Fill(this);
        var accent = Theme.Accent(this);
        var text = Theme.TextSecondary(this);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));  // hit-testable
        for (int i = 0; i < 10; i++)
        {
            int v = i < zones.Count ? zones[i] : 0;
            double x = i * (w + Gap);
            dc.DrawRoundedRectangle(track, null, new Rect(x, NumberSpace, w, h), 4, 4);
            if (v > 0)
            {
                double bh = h * v / 8;
                dc.DrawRoundedRectangle(Theme.WithOpacity(accent, 0.35 + 0.65 * v / 8), null,
                                        new Rect(x, NumberSpace + h - bh, w, bh), 4, 4);
            }
            var label = Theme.Label(this, v.ToString(), 12, text);
            dc.DrawText(label, new Point(x + (w - label.Width) / 2, 0));
            if (IsKeyboardFocused && i == _focus)
                dc.DrawRoundedRectangle(null, new Pen(Theme.Text(this), 2), new Rect(x - 2, NumberSpace - 2, w + 4, h + 4), 5, 5);
        }
        if (Pull > 0.01)
        {
            double px = Pull * ActualWidth;
            dc.DrawLine(new Pen(Theme.Text(this), 2), new Point(px, NumberSpace - 4), new Point(px, ActualHeight));
        }
    }

    void SetFrom(Point p)
    {
        var zones = Zones;
        if (zones is null)
            return;
        int i = Math.Clamp((int)(p.X / (ColumnWidth + Gap)), 0, 9);
        int v = Math.Clamp((int)Math.Round((1 - (p.Y - NumberSpace) / (ActualHeight - NumberSpace)) * 8), 0, 8);
        _focus = i;
        if (zones[i] != v)
        {
            var copy = new List<int>(zones) { [i] = v };
            Zones = copy;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        SetFrom(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            SetFrom(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var zones = Zones;
        if (zones is null)
            return;
        switch (e.Key)
        {
            case Key.Left: _focus = Math.Max(0, _focus - 1); InvalidateVisual(); break;
            case Key.Right: _focus = Math.Min(9, _focus + 1); InvalidateVisual(); break;
            case Key.Up or Key.Down:
                var copy = new List<int>(zones);
                copy[_focus] = Math.Clamp(copy[_focus] + (e.Key == Key.Up ? 1 : -1), 0, 8);
                Zones = copy;
                break;
            default: return;
        }
        e.Handled = true;
    }
}
