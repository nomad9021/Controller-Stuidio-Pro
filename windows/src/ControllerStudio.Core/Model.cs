// Presets and settings as bindable objects. The JSON shape matches presets.json written by
// the Python version (%APPDATA%\controller-studio-pro\presets.json), so existing presets carry over.
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ControllerStudio;

/// <summary>An observable object that also raises <see cref="Changed"/> when anything below it changes,
/// so a whole preset can be saved after any edit.</summary>
public abstract class Node : ObservableObject
{
    // Live values shown next to the settings; they aren't edits.
    static readonly HashSet<string> Transient = ["Pull", "Firing", "Builtin", "Modified", "Summary"];

    readonly Dictionary<string, object> _wired = [];

    public event EventHandler? Changed;

    protected Node()
    {
        // Field initializers have run by now, so wire up the initial children.
        foreach (var p in Props(GetType()))
            Wire(p.Name, p.GetValue(this));
    }

    static readonly Dictionary<Type, PropertyInfo[]> PropCache = [];

    static PropertyInfo[] Props(Type t)
    {
        lock (PropCache)
        {
            if (!PropCache.TryGetValue(t, out var props))
                PropCache[t] = props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0 && !Transient.Contains(p.Name)).ToArray();
            return props;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is null || Transient.Contains(e.PropertyName))
            return;
        var prop = Props(GetType()).FirstOrDefault(p => p.Name == e.PropertyName);
        if (prop is null)
            return;
        Wire(prop.Name, prop.GetValue(this));
        RaiseChanged();
    }

    void Wire(string name, object? value)
    {
        if (_wired.Remove(name, out var old))
        {
            if (old is Node n) n.Changed -= ChildChanged;
            if (old is INotifyCollectionChanged c) { c.CollectionChanged -= CollectionChanged; Items(old, false); }
        }
        if (value is Node node)
        {
            node.Changed += ChildChanged;
            _wired[name] = node;
        }
        else if (value is INotifyCollectionChanged coll)
        {
            coll.CollectionChanged += CollectionChanged;
            Items(value, true);
            _wired[name] = value;
        }
    }

    void Items(object list, bool on)
    {
        foreach (var item in (IEnumerable)list)
            if (item is Node n)
            {
                n.Changed -= ChildChanged;
                if (on) n.Changed += ChildChanged;
            }
    }

    void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (var item in e.OldItems)
                if (item is Node n) n.Changed -= ChildChanged;
        if (e.NewItems != null)
            foreach (var item in e.NewItems)
                if (item is Node n) n.Changed += ChildChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset && sender != null)
            Items(sender, true);
        RaiseChanged();
    }

    void ChildChanged(object? sender, EventArgs e) => RaiseChanged();

    void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>A deep copy (through JSON, so it is exactly what would be saved).</summary>
    public T Clone<T>() where T : Node => Json.Clone((T)this);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

// ------------------------------------------------------------------ presets

public partial class Preset : Node
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _summary = "";
    /// <summary>"none" (react to your inputs) or "game" (live game data).</summary>
    [ObservableProperty] private string _telemetry = "none";
    [ObservableProperty] private string _lightbar = "#ffffff";
    [ObservableProperty] private TriggerPair _triggers = new();

    [ObservableProperty]
    [property: JsonIgnore] private bool _builtin;
    [ObservableProperty]
    [property: JsonIgnore] private bool _modified;
}

public partial class TriggerPair : Node
{
    [ObservableProperty] private TriggerConfig _l2 = new();
    [ObservableProperty] private TriggerConfig _r2 = new();

    public TriggerConfig this[string side] => side == "l2" ? L2 : R2;
}

public partial class TriggerConfig : Node
{
    [ObservableProperty] private Feel _feel = new();
    [ObservableProperty] private ObservableCollection<Reaction> _reactions = [];
    [ObservableProperty] private TriggerOutput _output = new();
    [ObservableProperty] private bool _reactionsOn = true;

    /// <summary>How far the trigger is pulled right now, 0..1 (live, not saved).</summary>
    [ObservableProperty]
    [property: JsonIgnore] private double _pull;
}

public partial class Feel : Node
{
    /// <summary>off, zones, smooth, click or vibrate.</summary>
    [ObservableProperty] private string _type = "off";
    [ObservableProperty] private List<int> _zones = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    [ObservableProperty] private SmoothFeel _smooth = new();
    [ObservableProperty] private ClickFeel _click = new();
    [ObservableProperty] private VibrateFeel _vibrate = new();
}

public partial class SmoothFeel : Node
{
    [ObservableProperty] private int _start = 10;
    [ObservableProperty] private int _force = 40;
}

public partial class ClickFeel : Node
{
    [ObservableProperty] private int _start = 4;
    [ObservableProperty] private int _end = 6;
    [ObservableProperty] private int _force = 8;
}

public partial class VibrateFeel : Node
{
    [ObservableProperty] private int _start;
    [ObservableProperty] private int _amplitude = 5;
    [ObservableProperty] private int _freq = 40;
}

public partial class TriggerOutput : Node
{
    [ObservableProperty] private int _deadzone;
    [ObservableProperty] private int _fullAt = 100;
    [ObservableProperty] private int _curve;
    [ObservableProperty] private int _max = 100;
}

public partial class Reaction : Node
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private bool _enabled = true;
    /// <summary>buttons, slam, held, rumble, gear, abs, wheelspin or redline.</summary>
    [ObservableProperty] private string _when = "buttons";
    [ObservableProperty] private List<string> _buttons = [];
    [ObservableProperty] private ReactionEffect _effect = new();
    [ObservableProperty] private SlamCondition _slam = new();
    [ObservableProperty] private HeldCondition _held = new();
    [ObservableProperty] private RumbleCondition _rumble = new();

    public Reaction()
    {
        Changed += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    /// <summary>One line describing it, like "Circle or Square pressed → Kick 7 · 50 ms".</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var e = Effect ?? new ReactionEffect();
            string when = When switch
            {
                "buttons" => Buttons is { Count: > 0 } b ? string.Join(" or ", b.Select(Names.Button)) + " pressed" : "A button is pressed",
                "slam" => "Slammed",
                "held" => $"Held past {Held?.Above}%",
                "gear" => "Gear changes",
                "abs" => "Wheels lock up",
                "wheelspin" => "Wheelspin",
                "redline" => "Near the redline",
                "rumble" => $"Game rumbles over {Rumble?.Above ?? 30}%",
                _ => When,
            };
            string what = e.Type switch { "buzz" => "Buzz", "wall" => "Wall", _ => "Kick" };
            string freq = e.Type == "buzz" ? $" · {e.Freq} Hz" : "";
            string len = When is "buttons" or "gear" or "slam" ? $" · {e.DurationMs} ms" : "";
            return $"{when} → {what} {e.Strength}{freq}{len}";
        }
    }

    /// <summary>True while this reaction is driving the trigger (live, not saved).</summary>
    [ObservableProperty]
    [property: JsonIgnore] private bool _firing;
}

public partial class ReactionEffect : Node
{
    /// <summary>kick, buzz or wall.</summary>
    [ObservableProperty] private string _type = "kick";
    [ObservableProperty] private int _strength = 7;
    [ObservableProperty] private int _durationMs = 60;
    [ObservableProperty] private int _start;
    [ObservableProperty] private int _freq = 45;
}

public partial class SlamCondition : Node
{
    [ObservableProperty] private int _from = 30;
    [ObservableProperty] private int _to = 85;
    [ObservableProperty] private int _within = 200;
}

public partial class HeldCondition : Node
{
    [ObservableProperty] private int _above = 90;
}

public partial class RumbleCondition : Node
{
    [ObservableProperty] private int _above = 30;
    [ObservableProperty] private bool _scale = true;
}

// ------------------------------------------------------------------ settings

public partial class AppSettings : Node
{
    [ObservableProperty] private LightingSettings _lighting = new();
    [ObservableProperty] private OutputSettings _output = new();
    [ObservableProperty] private WindowsSettings _windows = new();
    /// <summary>The Linux window's glass settings; kept so a shared presets file round-trips.</summary>
    public JsonObject? App { get; set; }
}

public partial class LightingSettings : Node
{
    /// <summary>preset, custom or off.</summary>
    [ObservableProperty] private string _mode = "preset";
    [ObservableProperty] private string _color = "#2f6bff";
    /// <summary>solid, breathe, rainbow or battery.</summary>
    [ObservableProperty] private string _effect = "solid";
    [ObservableProperty] private int _speed = 5;
    [ObservableProperty] private int _brightness = 100;
    /// <summary>off, center, edges or all.</summary>
    [ObservableProperty] private string _playerLeds = "center";
}

public partial class OutputSettings : Node
{
    [ObservableProperty] private bool _virtual;
    [ObservableProperty] private Dictionary<string, string> _map = new()
    {
        ["paddle_left"] = "", ["paddle_right"] = "", ["fn_left"] = "", ["fn_right"] = "",
    };
}

public partial class WindowsSettings : Node
{
    /// <summary>Closing the window keeps effects running from the notification area.</summary>
    [ObservableProperty] private bool _runInBackground = true;
    [ObservableProperty] private bool _toldAboutTray;
}

public static class Names
{
    public static readonly Dictionary<string, string> Buttons = new()
    {
        [""] = "None", ["cross"] = "Cross", ["circle"] = "Circle", ["square"] = "Square", ["triangle"] = "Triangle",
        ["l1"] = "L1", ["r1"] = "R1", ["l2"] = "L2", ["r2"] = "R2", ["l3"] = "L3", ["r3"] = "R3",
        ["create"] = "Create", ["options"] = "Options", ["ps"] = "PS", ["touchpad"] = "Touchpad", ["mute"] = "Mute",
        ["up"] = "D-pad Up", ["down"] = "D-pad Down", ["left"] = "D-pad Left", ["right"] = "D-pad Right",
        ["paddle_left"] = "Back Left", ["paddle_right"] = "Back Right", ["fn_left"] = "Fn Left", ["fn_right"] = "Fn Right",
    };

    public static string Button(string id) => Buttons.GetValueOrDefault(id, id);

    /// <summary>Buttons a reaction can listen for.</summary>
    public static readonly string[] ReactionButtons =
    [
        "cross", "circle", "square", "triangle", "l1", "r1", "l3", "r3", "up", "down", "left", "right",
        "paddle_left", "paddle_right", "fn_left", "fn_right", "create", "options", "touchpad",
    ];

    /// <summary>What the Edge's extra buttons can act as on the virtual controller.</summary>
    public static readonly string[] MapTargets =
    [
        "", "cross", "circle", "square", "triangle", "l1", "r1", "l3", "r3", "create", "options", "up", "down", "left", "right",
    ];
}
