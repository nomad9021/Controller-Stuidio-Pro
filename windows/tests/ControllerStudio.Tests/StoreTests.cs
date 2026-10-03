using ControllerStudio;
using Xunit;

namespace ControllerStudio.Tests;

public class StoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("csp-test").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ReadsPresetsWrittenByThePythonVersion()
    {
        File.WriteAllText(Path.Combine(_dir, "presets.json"), """
            {"active": "mine", "version": 2, "hidden": ["shooter"], "telemetry_port": 5300,
             "overrides": {"motorfest": {"id": "motorfest", "name": "Motorfest", "summary": "", "telemetry": "none",
               "lightbar": "#ff0000", "triggers": {"l2": {"feel": {"type": "smooth", "zones": [0,0,0,0,0,0,0,0,0,0],
               "smooth": {"start": 3, "force": 9}, "click": {"start": 4, "end": 6, "force": 8},
               "vibrate": {"start": 0, "amplitude": 5, "freq": 40}}, "reactions": [], "output": {"deadzone": 4,
               "full_at": 90, "curve": 10, "max": 100}, "reactions_on": false}, "r2": {"feel": {"type": "off"},
               "reactions": [{"id": "x", "enabled": true, "when": "slam", "buttons": [], "effect": {"type": "buzz",
               "strength": 3, "duration_ms": 80, "start": 2, "freq": 33}, "slam": {"from": 10, "to": 70, "within": 150},
               "held": {"above": 90}}], "output": {"deadzone": 0, "full_at": 100, "curve": 0, "max": 100}}}}},
             "custom": [{"id": "mine", "name": "Mine", "summary": "s", "telemetry": "forza", "lightbar": "#00ff00",
               "triggers": {"l2": {"feel": {"type": "zones", "zones": [1,2,3]}}, "r2": {}}}],
             "settings": {"lighting": {"mode": "custom", "color": "#123456", "effect": "breathe", "speed": 3,
               "brightness": 50, "player_leds": "all"}, "output": {"virtual": true, "map": {"paddle_left": "cross"}},
               "app": {"glass": 55, "blur": true}}}
            """);
        var s = new PresetStore(_dir);
        Assert.Equal("mine", s.ActiveId);
        var all = s.All();
        Assert.DoesNotContain(all, p => p.Id == "shooter");
        var mf = all.Single(p => p.Id == "motorfest");
        Assert.True(mf.Builtin && mf.Modified);
        Assert.False(mf.Triggers.L2.ReactionsOn);
        Assert.Equal(90, mf.Triggers.L2.Output.FullAt);
        Assert.Equal(80, mf.Triggers.R2.Reactions[0].Effect.DurationMs);
        Assert.Equal(30, mf.Triggers.R2.Reactions[0].Rumble.Above);  // filled in
        var mine = s.Active();
        Assert.Equal("game", mine.Telemetry);
        Assert.Equal([1, 2, 3, 0, 0, 0, 0, 0, 0, 0], mine.Triggers.L2.Feel.Zones);
        Assert.Equal("all", s.Settings.Lighting.PlayerLeds);
        Assert.Equal("cross", s.Settings.Output.Map["paddle_left"]);
        Assert.Equal("", s.Settings.Output.Map["fn_right"]);

        // Saving keeps what the Linux window stored.
        s.SaveSettings(s.Settings);
        Assert.Contains("\"glass\": 55", File.ReadAllText(Path.Combine(_dir, "presets.json")));
    }

    [Fact]
    public void EditDuplicateDeleteRestore()
    {
        var s = new PresetStore(_dir);
        var p = s.Get("shooter")!;
        p.Triggers.R2.Feel.Click.Force = 3;
        s.Update(p);
        Assert.True(new PresetStore(_dir).Get("shooter")!.Modified);

        var copy = s.Duplicate("shooter");
        Assert.Equal("shooter-copy", copy.Id);
        Assert.Equal("shooter-copy-2", s.Duplicate("shooter").Id);
        Assert.Equal(3, copy.Triggers.R2.Feel.Click.Force);

        s.SetActive("shooter");
        s.Delete("shooter");
        Assert.Equal("off", s.ActiveId);
        Assert.Equal(1, s.HiddenCount);
        s.RestoreBuiltins();
        Assert.False(s.Get("shooter")!.Modified);  // deleting drops the edits
    }

    [Fact]
    public void ChangesBubbleUpForAutosave()
    {
        var p = Builtins.All()[0];
        int n = 0;
        p.Changed += (_, _) => n++;
        p.Triggers.L2.Feel.Smooth.Force = 99;
        p.Triggers.R2.Reactions[0].Effect.Strength = 2;
        p.Triggers.R2.Reactions.Add(new Reaction());
        p.Triggers.R2.Reactions[^1].Buttons = ["cross"];
        p.Triggers.L2.Pull = 0.5;  // live value, not an edit
        p.Triggers.L2.Reactions[0].Firing = true;
        Assert.Equal(4, n);

        var clone = p.Clone<Preset>();
        int m = 0;
        clone.Changed += (_, _) => m++;
        clone.Triggers.L2.Output.Max = 50;
        Assert.Equal(1, m);
        Assert.Equal(4, n);
    }
}
