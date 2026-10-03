namespace ControllerStudio;

/// <summary>The presets that ship with the app (same as the Linux version's presets.py).</summary>
public static class Builtins
{
    static List<int> Progressive(int top, int start = 2)
    {
        int n = 10 - start;
        var z = Enumerable.Repeat(0, start).ToList();
        for (int i = 0; i < n; i++)
            z.Add(Math.Max(1, (int)Math.Round(top * (i + 1) / (double)n, MidpointRounding.ToEven)));
        return z;
    }

    static TriggerConfig Trigger(string feel = "off", List<int>? zones = null, SmoothFeel? smooth = null,
                                 ClickFeel? click = null, params Reaction[] reactions) => new()
    {
        Feel = new Feel
        {
            Type = feel,
            Zones = zones ?? [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            Smooth = smooth ?? new SmoothFeel(),
            Click = click ?? new ClickFeel(),
        },
        Reactions = [.. reactions],
    };

    static Reaction React(string id, string when, ReactionEffect effect, string[]? buttons = null,
                          bool enabled = true, RumbleCondition? rumble = null) => new()
    {
        Id = id, When = when, Effect = effect, Buttons = [.. buttons ?? []], Enabled = enabled,
        Rumble = rumble ?? new RumbleCondition(),
    };

    static ReactionEffect Kick(int strength, int ms, int start = 0) =>
        new() { Type = "kick", Strength = strength, DurationMs = ms, Start = start, Freq = 45 };

    static ReactionEffect Buzz(int strength, int freq, int start = 0, int ms = 600) =>
        new() { Type = "buzz", Strength = strength, Freq = freq, Start = start, DurationMs = ms };

    static Preset P(string id, string name, string summary, string telemetry, string lightbar,
                    TriggerConfig l2, TriggerConfig r2) => new()
    {
        Id = id, Name = name, Summary = summary, Telemetry = telemetry, Lightbar = lightbar,
        Triggers = new TriggerPair { L2 = l2, R2 = r2 },
    };

    /// <summary>A fresh copy of every built-in preset.</summary>
    public static List<Preset> All() =>
    [
        P("motorfest", "The Crew Motorfest",
          "L2 stiffens and chatters when slammed; R2 is free with a hard kick on every shift.", "none", "#ff5a1f",
          Trigger("zones", Progressive(6), reactions:
          [
              React("slam", "slam", Buzz(7, 28, start: 4, ms: 600)),
              React("rumble", "rumble", Buzz(6, 30, start: 3), enabled: false),
          ]),
          Trigger(reactions: [React("shift", "buttons", Kick(7, 50), ["circle", "square"])])),
        P("forza-horizon", "Forza Horizon 5",
          "Uses live car data: real ABS lock-up, a kick on every gear change, wheelspin buzz.", "game", "#e8178a",
          Trigger("zones", Progressive(5), reactions: [React("abs", "abs", Buzz(6, 30, start: 4))]),
          Trigger(reactions: [React("gear", "gear", Kick(6, 60)), React("spin", "wheelspin", Buzz(4, 60, start: 3))])),
        P("forza-motorsport", "Forza Motorsport",
          "Sim-style: a firm wall on L2, light weight on R2, redline and wheelspin cues.", "game", "#2f6bff",
          Trigger("zones", [0, 0, 1, 2, 4, 6, 8, 8, 8, 8], reactions: [React("abs", "abs", Buzz(7, 32, start: 5))]),
          Trigger("smooth", smooth: new SmoothFeel { Start = 5, Force = 15 }, reactions:
          [
              React("gear", "gear", Kick(7, 50)),
              React("spin", "wheelspin", Buzz(5, 55, start: 3)),
              React("redline", "redline", Buzz(2, 80, start: 5)),
          ])),
        P("f1", "F1 25",
          "Live F1 data: brake lock-up chatter, a kick on every upshift, a buzz on the rev lights.", "game", "#e10600",
          Trigger("zones", [0, 0, 2, 3, 4, 6, 8, 8, 8, 8], reactions: [React("abs", "abs", Buzz(7, 35, start: 5))]),
          Trigger("smooth", smooth: new SmoothFeel { Start = 5, Force = 10 }, reactions:
          [
              React("gear", "gear", Kick(7, 45)),
              React("spin", "wheelspin", Buzz(5, 60, start: 3)),
              React("redline", "redline", Buzz(2, 90, start: 6)),
          ])),
        P("dirt-rally", "DiRT Rally 2.0",
          "Codemasters data (also DiRT 4 and GRID): loose-surface wheelspin, lock-ups and shift kicks.", "game", "#ffb000",
          Trigger("zones", Progressive(5), reactions: [React("abs", "abs", Buzz(6, 25, start: 4))]),
          Trigger(reactions: [React("gear", "gear", Kick(7, 50)), React("spin", "wheelspin", Buzz(4, 45, start: 2))])),
        P("beamng", "BeamNG.drive",
          "OutGauge data (also Live for Speed): ABS and traction-control lights drive the triggers.", "game", "#ff7a00",
          Trigger("zones", Progressive(6), reactions: [React("abs", "abs", Buzz(6, 30, start: 4))]),
          Trigger(reactions: [React("gear", "gear", Kick(6, 55)), React("spin", "wheelspin", Buzz(4, 55, start: 3))])),
        P("r6", "Rainbow Six Siege",
          "Light aim weight on L2, a click wall on R2, and recoil kicks driven by the game's own rumble.", "none", "#4aa3ff",
          Trigger("smooth", smooth: new SmoothFeel { Start = 5, Force = 20 }),
          Trigger("click", click: new ClickFeel { Start = 3, End = 5, Force = 6 }, reactions:
          [
              React("recoil", "rumble", Buzz(8, 14, start: 3), rumble: new RumbleCondition { Above = 25, Scale = true }),
          ])),
        P("shooter", "Shooter", "Light weight on aim, a crisp click-through wall on the trigger.", "none", "#3ccf6e",
          Trigger("smooth", smooth: new SmoothFeel { Start = 5, Force = 25 }),
          Trigger("click", click: new ClickFeel { Start = 4, End = 6, Force = 8 })),
        P("off", "Off", "Triggers feel like a normal controller.", "none", "#ffffff", Trigger(), Trigger()),
    ];

    public static readonly HashSet<string> Ids = [.. All().Select(p => p.Id)];
}
