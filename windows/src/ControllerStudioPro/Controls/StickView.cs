using System.Windows;
using System.Windows.Media;

namespace ControllerStudioPro.Controls;

/// <summary>Stick position with a trail of where it has been, to show its range.</summary>
public sealed class StickView : FrameworkElement
{
    readonly List<Point> _trail = [];
    Point _pos;

    public StickView()
    {
        Width = 150;
        Height = 150;
    }

    /// <summary>Rest drift (0..1) measured when the stick was last let go.</summary>
    public double? Drift { get; private set; }
    public Point Position => _pos;

    public void Update(byte x, byte y)
    {
        var p = new Point((x - 128) / 127.5, (y - 128) / 127.5);
        double mag = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        if (mag > 0.15)
        {
            _trail.Add(p);
            if (_trail.Count > 1500)
                _trail.RemoveAt(0);
        }
        else
            Drift = mag;
        if (p != _pos)
        {
            _pos = p;
            InvalidateVisual();
        }
    }

    public void Clear()
    {
        _trail.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Min(ActualWidth, ActualHeight), r = w / 2 - 8, cx = ActualWidth / 2, cy = ActualHeight / 2;
        var stroke = new Pen(Theme.FillStrong(this), 1.5);
        var thin = new Pen(Theme.Divider(this), 1);
        dc.DrawEllipse(Theme.Fill(this), stroke, new Point(cx, cy), r, r);
        dc.DrawLine(thin, new Point(cx - r, cy), new Point(cx + r, cy));
        dc.DrawLine(thin, new Point(cx, cy - r), new Point(cx, cy + r));
        dc.DrawEllipse(null, thin, new Point(cx, cy), r * 0.1, r * 0.1);
        var accent = Theme.Accent(this);
        var dot = Theme.WithOpacity(accent, 0.35);
        foreach (var p in _trail)
            dc.DrawRectangle(dot, null, new Rect(cx + p.X * r - 1.25, cy + p.Y * r - 1.25, 2.5, 2.5));
        dc.DrawEllipse(accent, new Pen(Brushes.White, 2), new Point(cx + _pos.X * r, cy + _pos.Y * r), 8, 8);
    }
}
