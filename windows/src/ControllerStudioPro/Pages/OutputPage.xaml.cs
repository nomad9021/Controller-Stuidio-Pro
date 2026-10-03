using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using ControllerStudio;

namespace ControllerStudioPro.Pages;

/// <summary>Virtual controller, button mapping and app behaviour (its DataContext is the output settings).</summary>
public partial class OutputPage : UserControl, ILivePage
{
    readonly MainWindow _owner;
    readonly OutputSettings _o;
    readonly List<ComboBox> _maps = [];
    int _tick;

    public OutputPage(MainWindow owner)
    {
        _owner = owner;
        _o = owner.Settings.Output;
        InitializeComponent();
        DataContext = _o;
        BackgroundSwitch.SetBinding(ToggleButton.IsCheckedProperty,
            new Binding(nameof(WindowsSettings.RunInBackground)) { Source = owner.Settings.Windows });
        StartupSwitch.IsChecked = Native.StartsWithWindows;

        foreach (var key in new[] { "paddle_left", "paddle_right", "fn_left", "fn_right" })
        {
            var row = new Grid { Style = (Style)FindResource("Row") };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = Names.Button(key), Style = (Style)FindResource("Strong"), VerticalAlignment = VerticalAlignment.Center });
            var box = new ComboBox { MinWidth = 220, Tag = key };
            AutomationProperties.SetName(box, Names.Button(key) + " sends");
            foreach (var t in Names.MapTargets)
                box.Items.Add(new ComboBoxItem { Content = t == "" ? "Nothing (reactions only)" : $"Acts as {Names.Button(t)}", Tag = t });
            box.SelectedIndex = Math.Max(0, Array.IndexOf(Names.MapTargets, _o.Map.GetValueOrDefault(key, "")));
            box.SelectionChanged += (_, _) =>
            {
                var target = (string)((ComboBoxItem)box.SelectedItem).Tag;
                _o.Map = new Dictionary<string, string>(_o.Map) { [key] = target };
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            MapRows.Children.Add(row);
            _maps.Add(box);
        }
    }

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Native.StartsWithWindows = StartupSwitch.IsChecked == true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(_owner, ex.Message, "Controller Studio Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        StartupSwitch.IsChecked = Native.StartsWithWindows;
    }

    void Pill(Border pill, TextBlock text, string label, string? kind)
    {
        text.Text = label;
        pill.SetResourceReference(Border.BackgroundProperty, kind switch
        {
            "good" => "SystemFillColorSuccessBackgroundBrush",
            "warn" => "SystemFillColorCautionBackgroundBrush",
            _ => "ControlFillColorSecondaryBrush",
        });
        text.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            "good" => "SystemFillColorSuccessBrush",
            "warn" => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
        });
    }

    public void Update(EngineSnapshot snap)
    {
        if (_tick++ % 15 != 0)  // a few times a second is plenty here
            return;
        bool driver = VirtualPad.Available;
        VirtualSwitch.IsEnabled = _o.Virtual || driver;
        foreach (var m in _maps)
            m.IsEnabled = _o.Virtual;
        if (driver)
        {
            Pill(AccessPill, AccessPillText, "Ready", "good");
            AccessText.Text = "The ViGEmBus driver is installed, so Controller Studio Pro can create a virtual controller.";
        }
        else
        {
            Pill(AccessPill, AccessPillText, "Needs setup", "warn");
            AccessText.Text = "The ViGEmBus driver isn't installed. Run the installer again to add it.";
        }

        bool hidden = Native.MoonlightHidden;
        if (_o.Virtual)
        {
            Pill(MoonPill, MoonPillText, hidden ? "Set up" : "Not set up", hidden ? "good" : "warn");
            MoonText.Text = hidden
                ? "Moonlight ignores the real controller and uses the virtual one. Restart Moonlight after turning this on or off."
                : "Moonlight still sees the real controller.";
        }
        else
        {
            Pill(MoonPill, MoonPillText, "Real controller", null);
            MoonText.Text = "Moonlight uses your controller directly.";
        }

        if (!_o.Virtual)
        {
            Pill(StatePill, StatePillText, "Off", null);
            StateText.Text = "Games get your controller as-is.";
        }
        else if (snap.VirtualActive)
        {
            Pill(StatePill, StatePillText, "Running", "good");
            StateText.Text = "A virtual Xbox controller is live with the active preset's throw settings. If a game reacts to both controllers, hide the real one with HidHide.";
        }
        else if (snap.VirtualError is { } err)
        {
            Pill(StatePill, StatePillText, "Error", "warn");
            StateText.Text = err;
        }
        else
        {
            Pill(StatePill, StatePillText, "Waiting", null);
            StateText.Text = "Starts when the controller connects.";
        }
    }
}
