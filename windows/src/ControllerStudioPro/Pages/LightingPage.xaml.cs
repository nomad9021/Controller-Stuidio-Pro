using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ControllerStudio;

namespace ControllerStudioPro.Pages;

/// <summary>Light bar and player lights (its DataContext is the lighting settings).</summary>
public partial class LightingPage : UserControl, ILivePage
{
    static readonly string[] Colors =
    [
        "#ff3b30", "#ff5a1f", "#ff9f0a", "#ffd60a", "#34c759", "#00c7be", "#32ade6",
        "#2f6bff", "#5e5ce6", "#bf5af2", "#ff2d92", "#ffffff",
    ];

    readonly MainWindow _owner;
    readonly LightingSettings _l;
    string? _shownLight;
    byte _shownLeds = 0xFF;

    public LightingPage(MainWindow owner)
    {
        _owner = owner;
        _l = owner.Settings.Lighting;
        InitializeComponent();
        DataContext = _l;
        foreach (var c in Colors)
        {
            var b = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = HexToBrush.Brush(c),
                GroupName = "lightcolor",
                ToolTip = c,
                Tag = c,
            };
            AutomationProperties.SetName(b, c);
            b.Checked += (_, _) => SetColor(c);
            Swatches.Children.Add(b);
        }
        _l.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    void SetColor(string c)
    {
        if (!string.Equals(_l.Color, c, StringComparison.OrdinalIgnoreCase))
            _l.Color = c;
    }

    void Refresh()
    {
        foreach (RadioButton b in Swatches.Children)
            b.IsChecked = string.Equals((string)b.Tag, _l.Color, StringComparison.OrdinalIgnoreCase);
        if (!Hex.IsKeyboardFocused)
            Hex.Text = _l.Color;
        Caption.Text = _l.Mode switch
        {
            "off" => "The light bar stays off whenever the controller is connected.",
            "custom" => "The light bar shows your color whenever the controller is connected.",
            _ => "The light bar shows the preset's color whenever the controller is connected.",
        };
    }

    public void Update(EngineSnapshot snap)
    {
        var light = snap.Light is { } c && c != (0, 0, 0) ? Lighting.Hex(c) : null;
        if (light != _shownLight)
        {
            _shownLight = light;
            Brush b = light is null ? Brushes.Transparent : HexToBrush.Brush(light);
            BarL.Stroke = BarR.Stroke = BarGlowL.Stroke = BarGlowR.Stroke = b;
        }
        if (snap.PlayerLeds != _shownLeds)
        {
            _shownLeds = snap.PlayerLeds;
            int i = 0;
            foreach (Rectangle r in Leds.Children)
            {
                bool on = (snap.PlayerLeds & (1 << i++)) != 0;
                r.SetResourceReference(Shape.FillProperty, on ? "TextFillColorPrimaryBrush" : "ControlStrongFillColorDisabledBrush");
            }
        }
    }

    void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (Native.PickColor(_owner, _l.Color) is { } c)
            _l.Color = c;
    }

    void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Hex_Commit(sender, e);
    }

    void Hex_Commit(object sender, RoutedEventArgs e)
    {
        var v = "#" + Hex.Text.Trim().TrimStart('#');
        if (Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$"))
            _l.Color = v.ToLowerInvariant();
        Hex.Text = _l.Color;
    }
}
