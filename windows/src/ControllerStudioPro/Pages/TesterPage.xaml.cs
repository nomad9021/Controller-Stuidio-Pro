using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ControllerStudio;

namespace ControllerStudioPro.Pages;

/// <summary>Everything the controller reports, live.</summary>
public partial class TesterPage : UserControl, ILivePage
{
    InputState? _last;

    public TesterPage(MainWindow owner)
    {
        InitializeComponent();
        SizeChanged += (_, _) => Cards.Columns = ActualWidth >= 820 ? 2 : 1;
        _ = owner;
    }

    static string Signed(double v, int digits) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("F" + digits);

    public void Update(EngineSnapshot snap)
    {
        var d = snap.Device;
        InfoModel.Text = d?.Model ?? "Not connected";
        InfoConn.Text = d is null ? "–" : d.Bluetooth ? "Bluetooth" : "USB";
        InfoAddr.Text = string.IsNullOrEmpty(d?.Address) ? "–" : d.Address.ToUpperInvariant();
        InfoRate.Text = d is null ? "–" : $"{snap.Rate} reports/s";
        var st = snap.Input;
        InfoBatt.Text = st is null ? "–" : $"{st.Battery}% · {st.Charging}";
        if (ReferenceEquals(st, _last))
            return;
        _last = st;
        Pad.Update(st, d?.Model.Contains("Edge") == true);
        if (st is null)
            return;

        Pressed.Text = st.Buttons.Count > 0 ? string.Join(" + ", st.Buttons.Select(Names.Button)) : "No buttons pressed";
        StickL.Update(st.LX, st.LY);
        StickR.Update(st.RX, st.RY);
        LVal.Text = Stick(StickL);
        RVal.Text = Stick(StickR);

        Meter(TrackL2, FillL2, OutL2, PctL2, st.L2, snap.OutL2);
        Meter(TrackR2, FillR2, OutR2, PctR2, st.R2, snap.OutR2);

        TouchArea.Children.Clear();
        foreach (var t in st.Touches)
        {
            var dot = new Grid { Width = 28, Height = 28 };
            dot.Children.Add(new Ellipse { Fill = Pad_Accent(), Stroke = Brushes.White, StrokeThickness = 2 });
            dot.Children.Add(new TextBlock
            {
                Text = (t.Id % 10).ToString(), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            Canvas.SetLeft(dot, t.X / 1920.0 * 384 - 14);
            Canvas.SetTop(dot, t.Y / 1080.0 * 216 - 14);
            TouchArea.Children.Add(dot);
        }
        TouchRead.Text = st.Touches.Count > 0 ? string.Join("   ", st.Touches.Select(t => $"#{t.Id}: {t.X}, {t.Y}")) : "No touches";

        var (ax, ay, az) = st.Accel;
        var (gx, gy, gz) = st.Gyro;
        double pitch = Math.Atan2(az, ay) * 180 / Math.PI, roll = Math.Atan2(ax, ay) * 180 / Math.PI;
        TiltRotate.Angle = roll;
        TiltScale.ScaleY = Math.Max(0.15, Math.Abs(Math.Cos(pitch * Math.PI / 180)));
        TiltRead.Text = $"pitch {Signed(pitch, 0)}°  roll {Signed(roll, 0)}°";
        GyroRead.Text = string.Join("  ", new[] { gx, gy, gz }.Select(v => Signed(v / 16.4, 0).PadLeft(4))) + " °/s";
        AccelRead.Text = string.Join("  ", new[] { ax, ay, az }.Select(v => Signed(v / 8192.0, 2))) + " g";
    }

    Brush Pad_Accent() => Controls.Theme.Accent(this);

    static string Stick(Controls.StickView s)
    {
        var drift = s.Drift is { } d ? $"  ·  drift {d * 100:F1}%" : "";
        return $"{Signed(s.Position.X, 2)}, {Signed(-s.Position.Y, 2)}{drift}";
    }

    static void Meter(FrameworkElement track, FrameworkElement fill, FrameworkElement output, TextBlock pct, byte raw, double outPct)
    {
        double w = track.ActualWidth;
        fill.Width = raw / 255.0 * w;
        output.Margin = new Thickness(Math.Max(0, outPct / 100 * w - 1.5), -3, 0, -3);
        pct.Text = $"{Math.Round(raw / 255.0 * 100)}%";
    }

    void ClearTrails_Click(object sender, RoutedEventArgs e)
    {
        StickL.Clear();
        StickR.Clear();
    }

    void LabKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LabFreq != null)
            LabFreq.Visibility = (LabKind.SelectedItem as ListBoxItem)?.Tag as string == "vibrate" ? Visibility.Visible : Visibility.Collapsed;
    }

    void LabGo_Click(object sender, RoutedEventArgs e)
    {
        int s = (int)LabStrength.Value, step = Math.Max(1, (int)Math.Ceiling(s / 12.5));
        var feel = new Feel
        {
            Type = (string)((ListBoxItem)LabKind.SelectedItem).Tag,
            Zones = [.. Enumerable.Repeat(step, 10)],
            Smooth = new SmoothFeel { Start = 0, Force = s },
            Click = new ClickFeel { Start = 4, End = 6, Force = step },
            Vibrate = new VibrateFeel { Start = 0, Amplitude = step, Freq = (int)LabFreq.Value },
        };
        App.Engine.TestFeel((string)((ListBoxItem)LabSide.SelectedItem).Tag, feel, 10);
    }

    void LabStop_Click(object sender, RoutedEventArgs e) => App.Engine.StopTest();

    void Rumble_Click(object sender, RoutedEventArgs e) =>
        App.Engine.Rumble(RumStrong.Value / 100, RumWeak.Value / 100, 1000);
}
