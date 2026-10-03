using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ControllerStudio;

/// <summary>What presets.json holds.</summary>
public class StoreData
{
    public string Active { get; set; } = "motorfest";
    public Dictionary<string, Preset> Overrides { get; set; } = [];
    public List<Preset> Custom { get; set; } = [];
    public List<string> Hidden { get; set; } = [];
    public int TelemetryPort { get; set; } = 5300;
    public AppSettings Settings { get; set; } = new();
    public int Version { get; set; } = 2;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Built-in presets, the user's edits, and app-wide settings.
/// Thread-safe; everything handed out is a copy.</summary>
public sealed partial class PresetStore
{
    /// <summary>%APPDATA%\controller-studio-pro (~/.config/controller-studio-pro elsewhere).</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "controller-studio-pro");

    readonly Lock _lock = new();
    readonly string _path;
    readonly StoreData _data;

    public PresetStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? DefaultDirectory, "presets.json");
        try
        {
            _data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_path), Json.Options) ?? new StoreData();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            if (File.Exists(_path))  // unreadable: keep it rather than overwrite someone's presets
                try { File.Copy(_path, _path + ".broken", true); } catch (IOException) { }
            _data = new StoreData();
        }
        _data.Settings ??= new AppSettings();
        _data.Settings.Lighting ??= new LightingSettings();
        _data.Settings.Output ??= new OutputSettings();
        _data.Settings.Windows ??= new WindowsSettings();
        foreach (var k in new[] { "paddle_left", "paddle_right", "fn_left", "fn_right" })
            _data.Settings.Output.Map.TryAdd(k, "");
        foreach (var p in _data.Overrides.Values.Concat(_data.Custom))
            Normalize(p);
        if (!File.Exists(_path))
            Save();
    }

    /// <summary>Fill in anything missing from presets written by older versions.</summary>
    static void Normalize(Preset p)
    {
        if (p.Telemetry == "forza") p.Telemetry = "game";
        p.Triggers ??= new TriggerPair();
        foreach (var t in new[] { p.Triggers.L2 ??= new(), p.Triggers.R2 ??= new() })
        {
            t.Feel ??= new Feel();
            if (t.Feel.Zones is not { Count: 10 })
                t.Feel.Zones = [.. (t.Feel.Zones ?? []).Concat(Enumerable.Repeat(0, 10)).Take(10)];
            t.Output ??= new TriggerOutput();
            t.Reactions ??= [];
            foreach (var r in t.Reactions)
            {
                r.Effect ??= new(); r.Slam ??= new(); r.Held ??= new(); r.Rumble ??= new(); r.Buttons ??= [];
            }
        }
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json.Options));
        File.Move(tmp, _path, true);
    }

    // ---- settings

    public AppSettings Settings
    {
        get { lock (_lock) return _data.Settings.Clone<AppSettings>(); }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_lock)
        {
            _data.Settings = settings.Clone<AppSettings>();
            Save();
        }
    }

    // ---- presets

    public List<Preset> All()
    {
        lock (_lock) return AllUnlocked();
    }

    List<Preset> AllUnlocked()
    {
        var output = new List<Preset>();
        foreach (var p in Builtins.All())
        {
            if (_data.Hidden.Contains(p.Id))
                continue;
            var modified = _data.Overrides.TryGetValue(p.Id, out var o);
            var cur = modified ? o!.Clone<Preset>() : p;
            cur.Builtin = true;
            cur.Modified = modified;
            output.Add(cur);
        }
        foreach (var p in _data.Custom)
            output.Add(p.Clone<Preset>());
        return output;
    }

    public Preset? Get(string id) => All().FirstOrDefault(p => p.Id == id);

    public string ActiveId
    {
        get { lock (_lock) return _data.Active; }
    }

    public Preset Active()
    {
        lock (_lock)
        {
            var all = AllUnlocked();
            return all.FirstOrDefault(p => p.Id == _data.Active) ?? all.FirstOrDefault(p => p.Id == "off") ?? all[0];
        }
    }

    public int HiddenCount
    {
        get { lock (_lock) return _data.Hidden.Count; }
    }

    public void SetActive(string id)
    {
        lock (_lock)
        {
            if (AllUnlocked().All(p => p.Id != id))
                throw new KeyNotFoundException(id);
            _data.Active = id;
            Save();
        }
    }

    public void Update(Preset preset)
    {
        var copy = preset.Clone<Preset>();
        lock (_lock)
        {
            if (Builtins.Ids.Contains(copy.Id))
                _data.Overrides[copy.Id] = copy;
            else
            {
                var i = _data.Custom.FindIndex(p => p.Id == copy.Id);
                if (i < 0)
                    throw new KeyNotFoundException(copy.Id);
                _data.Custom[i] = copy;
            }
            Save();
        }
    }

    public Preset Duplicate(string id)
    {
        lock (_lock)
        {
            var all = AllUnlocked();
            var src = all.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id);
            var copy = src.Clone<Preset>();
            copy.Name = src.Name + " Copy";
            var baseId = NonSlug().Replace(copy.Name.ToLowerInvariant(), "-").Trim('-');
            if (baseId.Length == 0) baseId = "preset";
            var ids = all.Select(p => p.Id).ToHashSet();
            copy.Id = baseId;
            for (int n = 2; ids.Contains(copy.Id); n++)
                copy.Id = $"{baseId}-{n}";
            _data.Custom.Add(copy);
            Save();
            return copy.Clone<Preset>();
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            if (AllUnlocked().Count <= 1)
                throw new InvalidOperationException("Keep at least one preset.");
            if (Builtins.Ids.Contains(id))
            {
                // Built-ins are hidden so they can be restored.
                if (!_data.Hidden.Contains(id))
                    _data.Hidden.Add(id);
                _data.Overrides.Remove(id);
            }
            else
                _data.Custom.RemoveAll(p => p.Id == id);
            var remaining = AllUnlocked().Select(p => p.Id).ToList();
            if (!remaining.Contains(_data.Active))
                _data.Active = remaining.Contains("off") ? "off" : remaining[0];
            Save();
        }
    }

    public void RestoreBuiltins()
    {
        lock (_lock)
        {
            _data.Hidden.Clear();
            Save();
        }
    }

    public void Reset(string id)
    {
        lock (_lock)
        {
            _data.Overrides.Remove(id);
            Save();
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
