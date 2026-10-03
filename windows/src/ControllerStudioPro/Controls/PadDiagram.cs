using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ControllerStudio;

namespace ControllerStudioPro.Controls;

/// <summary>A live DualSense (Edge) drawing: buttons light up, sticks move, triggers fill.</summary>
public sealed class PadDiagram : Viewbox
{
    readonly Canvas _c = new() { Width = 640, Height = 400 };
    readonly Dictionary<string, Shape> _buttons = [];
    readonly Rectangle _l2Fill, _r2Fill;
    readonly Ellipse _stickL, _stickR;
    readonly Canvas _touches = new();
    readonly List<UIElement> _edgeOnly = [];

    public PadDiagram()
    {
        Child = _c;
        Stretch = Stretch.Uniform;
        MaxHeight = 360;
        (_, _l2Fill) = Trigger("L2", 100);
        (_, _r2Fill) = Trigger("R2", 444);
        Btn("l1", Rect(96, 46, 104, 14, 7));
        Btn("r1", Rect(440, 46, 104, 14, 7));
        Add(new Path
        {
            Data = Geometry.Parse("M170 66C120 66 92 82 76 118L32 290C20 340 52 386 96 381C126 378 141 356 156 331L200 262C210 249 224 245 240 245H400C416 245 430 249 440 262L484 331C499 356 514 378 544 381C588 386 620 340 608 290L564 118C548 82 520 66 470 66Z"),
            StrokeThickness = 2,
        }, "body");
        _edgeOnly.Add(Btn("paddle_left", Rect(58, 300, 70, 38, 14)));
        _edgeOnly.Add(Btn("paddle_right", Rect(512, 300, 70, 38, 14)));
        Btn("touchpad", Rect(222, 70, 196, 104, 16));
        _c.Children.Add(_touches);
        Btn("create", Rect(190, 84, 12, 26, 6));
        Btn("options", Rect(438, 84, 12, 26, 6));
        Btn("up", PathShape("M118,104 L142,104 L142,130 L130,142 L118,130 Z"));
        Btn("down", PathShape("M118,196 L142,196 L142,170 L130,158 L118,170 Z"));
        Btn("left", PathShape("M84,138 L84,162 L110,162 L122,150 L110,138 Z"));
        Btn("right", PathShape("M176,138 L176,162 L150,162 L138,150 L150,138 Z"));
        Btn("triangle", Circle(510, 116, 16));
        Btn("circle", Circle(544, 150, 16));
        Btn("cross", Circle(510, 184, 16));
        Btn("square", Circle(476, 150, 16));
        foreach (var g in new[] { "M510,107 L517.5,120 L502.5,120 Z", "M504,178 L516,190 M516,178 L504,190", "M470,144 L482,144 L482,156 L470,156 Z" })
            Add(new Path { Data = Geometry.Parse(g), StrokeThickness = 1.8, IsHitTestVisible = false }, "glyph");
        Add(new Ellipse { Width = 14, Height = 14, StrokeThickness = 1.8, IsHitTestVisible = false }, "glyph", 537, 143);
        Add(Circle(232, 214, 34), "well");
        Add(Circle(408, 214, 34), "well");
        _stickL = (Ellipse)Btn("l3", Circle(232, 214, 22));
        _stickR = (Ellipse)Btn("r3", Circle(408, 214, 22));
        Btn("ps", Circle(320, 214, 13));
        Btn("mute", Rect(308, 236, 24, 9, 4.5));
        _edgeOnly.Add(Btn("fn_left", Rect(262, 252, 22, 10, 5)));
        _edgeOnly.Add(Btn("fn_right", Rect(356, 252, 22, 10, 5)));
        foreach (var (t, x) in new[] { ("Back L", 93.0), ("Back R", 547.0) })
        {
            var tb = new TextBlock { Text = t, FontSize = 12 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            tb.Loaded += (_, _) => Canvas.SetLeft(tb, x - tb.ActualWidth / 2);
            Canvas.SetTop(tb, 344);
            _c.Children.Add(tb);
            _edgeOnly.Add(tb);
        }
        Loaded += (_, _) => Recolor();
    }

    static Shape Rect(double x, double y, double w, double h, double r)
    {
        var s = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r };
        Canvas.SetLeft(s, x);
        Canvas.SetTop(s, y);
        return s;
    }

    static Shape Circle(double cx, double cy, double r)
    {
        var s = new Ellipse { Width = r * 2, Height = r * 2 };
        Canvas.SetLeft(s, cx - r);
        Canvas.SetTop(s, cy - r);
        return s;
    }

    static Shape PathShape(string data) => new Path { Data = Geometry.Parse(data) };

    T Add<T>(T el, string role, double? x = null, double? y = null) where T : Shape
    {
        el.Tag = role;
        if (x is { } xx) Canvas.SetLeft(el, xx);
        if (y is { } yy) Canvas.SetTop(el, yy);
        _c.Children.Add(el);
        return el;
    }

    Shape Btn(string name, Shape s)
    {
        s.StrokeThickness = 1.5;
        s.ToolTip = name;
        _buttons[name] = Add(s, "button");
        return s;
    }

    (Rectangle Bg, Rectangle Fill) Trigger(string label, double x)
    {
        var bg = (Rectangle)Add(Rect(x, 8, 96, 30, 9), "button");
        var fill = (Rectangle)Add(Rect(x, 8, 0, 30, 9), "fill");
        var tb = new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Canvas.SetLeft(tb, x + 38);
        Canvas.SetTop(tb, 13);
        _c.Children.Add(tb);
        return (bg, fill);
    }

    /// <summary>Re-read theme colours (call on theme change).</summary>
    public void Recolor()
    {
        var muted = Theme.TextSecondary(this);
        var fill = Theme.Fill(this);
        var stroke = Theme.Stroke(this);
        var accent = Theme.Accent(this);
        foreach (var child in _c.Children.OfType<Shape>())
        {
            switch (child.Tag as string)
            {
                case "body":
                    child.Fill = Theme.WithOpacity(fill, 0.6);
                    child.Stroke = Theme.FillStrong(this);
                    break;
                case "glyph":
                    child.Stroke = muted;
                    child.Fill = null;
                    break;
                case "well":
                    child.Fill = null;
                    child.Stroke = stroke;
                    break;
                case "fill":
                    child.Fill = accent;
                    break;
                default:
                    child.Fill = Brushes.Transparent;
                    child.Stroke = Theme.FillStrong(this);
                    break;
            }
        }
        _pressedFill = accent;
        _idle = Theme.Get(this, "ControlFillColorDefaultBrush", Brushes.WhiteSmoke);
        foreach (var b in _buttons.Values)
            b.Fill = _idle;
    }

    Brush _pressedFill = Brushes.DodgerBlue, _idle = Brushes.WhiteSmoke;

    public void Update(InputState? st, bool edge)
    {
        foreach (var el in _edgeOnly)
            el.Opacity = edge ? 1 : 0.25;
        if (st is null)
        {
            foreach (var b in _buttons.Values) b.Fill = _idle;
            _l2Fill.Width = _r2Fill.Width = 0;
            _touches.Children.Clear();
            return;
        }
        var down = st.Buttons.ToHashSet();
        foreach (var (name, shape) in _buttons)
            shape.Fill = down.Contains(name) ? _pressedFill : _idle;
        _l2Fill.Width = st.L2 / 255.0 * 96;
        _r2Fill.Width = st.R2 / 255.0 * 96;
        Canvas.SetLeft(_stickL, 232 - 22 + (st.LX - 128) / 128.0 * 13);
        Canvas.SetTop(_stickL, 214 - 22 + (st.LY - 128) / 128.0 * 13);
        Canvas.SetLeft(_stickR, 408 - 22 + (st.RX - 128) / 128.0 * 13);
        Canvas.SetTop(_stickR, 214 - 22 + (st.RY - 128) / 128.0 * 13);
        _touches.Children.Clear();
        foreach (var t in st.Touches)
        {
            var dot = new Ellipse { Width = 14, Height = 14, Fill = _pressedFill, Stroke = Brushes.White, StrokeThickness = 2 };
            Canvas.SetLeft(dot, 222 + t.X / 1920.0 * 196 - 7);
            Canvas.SetTop(dot, 70 + t.Y / 1080.0 * 104 - 7);
            _touches.Children.Add(dot);
        }
    }
}
