using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ControllerStudio;

namespace ControllerStudioPro.Pages;

/// <summary>Editor for a preset (its DataContext is the draft Preset).</summary>
public partial class PresetPage : UserControl, ILivePage
{
    static readonly (string Game, string How)[] Games =
    [
        ("Forza Horizon 4 / 5, Forza Motorsport", "Settings › HUD and Gameplay › Data Out: On · IP {ip} · Port 5300"),
        ("F1 22 / 23 / 24 / 25", "Settings › Telemetry Settings › UDP Telemetry: On · IP {ip} · Port 20777"),
        ("DiRT Rally 2.0, DiRT 4, GRID", "In Documents\\My Games\\<game>\\hardwaresettings\\hardware_settings_config.xml set <udp enabled=\"true\" extradata=\"3\" ip=\"{ip}\" port=\"20777\" delay=\"1\" />"),
        ("BeamNG.drive", "Options › Other › OutGauge support: On · IP {ip} · Port 4444"),
        ("Live for Speed", "In cfg.txt: OutGauge Mode 1, OutGauge IP {ip}, OutGauge Port 4444"),
        ("Any other game (The Crew Motorfest, Rainbow Six Siege, …)",
         "Most games don't share live data. Use a Game rumble reaction instead: it reacts whenever the game shakes the controller. It needs the Virtual Controller (Output)."),
    ];

    readonly MainWindow _owner;

    public PresetPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        L2Card.Attach(owner, "l2");
        R2Card.Attach(owner, "r2");
        SizeChanged += (_, _) =>
        {
            bool wide = ActualWidth >= 940;
            TriggerGrid.Columns = wide ? 2 : 1;
            L2Card.Margin = wide ? new Thickness(0, 0, 6, 0) : new Thickness(0);
            R2Card.Margin = wide ? new Thickness(6, 0, 0, 0) : new Thickness(0);
        };

        // 127.0.0.1 works when the game runs on this PC; another PC needs this one's address.
        var ip = owner.Addresses.FirstOrDefault() ?? "this computer's IP address";
        IpHeader.Text = $"this computer ({ip}, or 127.0.0.1 for games on this PC)";
        foreach (var (game, how) in Games)
        {
            GameSetup.Children.Add(new TextBlock { Text = game, Style = (Style)FindResource("Strong"), Margin = new Thickness(0, 8, 0, 2) });
            GameSetup.Children.Add(new TextBlock { Text = how.Replace("{ip}", ip), Style = (Style)FindResource("Hint") });
        }
    }

    Preset? Draft => DataContext as Preset;

    public void Show(Preset draft)
    {
        if (Draft is { } old)
            old.PropertyChanged -= DraftPropertyChanged;
        DataContext = draft;
        L2Card.DataContext = draft.Triggers.L2;
        R2Card.DataContext = draft.Triggers.R2;
        ColorHex.Text = draft.Lightbar;
        draft.PropertyChanged += DraftPropertyChanged;
        UpdateSource();
    }

    void DraftPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Preset.Telemetry))
            UpdateSource();
    }

    void UpdateSource()
    {
        if (Draft is not { } p)
            return;
        SourceHint.Text = p.Telemetry != "none"
            ? "Live data from Forza, F1, DiRT / GRID, BeamNG.drive or Live for Speed. Game-data reactions only work in this mode."
            : "Works in any game. Reactions follow your own presses, or the game's rumble.";
    }

    public void Update(EngineSnapshot snap)
    {
        L2Card.Update(snap);
        R2Card.Update(snap);
        if (Draft is not { Telemetry: not "none" })
            return;
        bool on = snap.TelemetryActive;
        var gear = snap.Gear switch { < 0 => "R", 0 => "N", { } g => g.ToString(), null => "–" };
        TeleState.Text = on ? $"Receiving {snap.TelemetrySource} data · gear {gear} · {snap.SpeedKmh} km/h" : "Waiting for game data…";
        TeleHelp.Text = on ? "Reactions are following the car."
            : "Start a supported game with its telemetry pointed at this computer (see Set up game data).";
        TeleDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            on ? "SystemFillColorSuccessBrush" : "ControlStrongFillColorDisabledBrush");
    }

    // ---- preset colour

    void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (Draft is { } p && Native.PickColor(_owner, p.Lightbar) is { } c)
        {
            p.Lightbar = c;
            ColorHex.Text = c;
        }
    }

    void ColorHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ColorHex_Commit(sender, e);
    }

    void ColorHex_Commit(object sender, RoutedEventArgs e)
    {
        if (Draft is not { } p)
            return;
        var v = "#" + ColorHex.Text.Trim().TrimStart('#');
        if (Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$"))
            p.Lightbar = v.ToLowerInvariant();
        ColorHex.Text = p.Lightbar;
    }
}
