using System.Text.Json;
using System.Text.Json.Nodes;
using ControllerStudio;
using Xunit;

namespace ControllerStudio.Tests;

/// <summary>The C# engine must behave exactly like the Python one it replaces
/// (fixtures/python.json is generated from controllerstudio/*.py).</summary>
public class MatchesPythonTests
{
    static readonly JsonNode Fx = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "python.json")))!;

    static byte[] Bytes(JsonNode? n) => n!.AsArray().Select(x => (byte)(int)x!).ToArray();
    static int[] Ints(JsonNode? n) => n!.AsArray().Select(x => (int)x!).ToArray();
    static T From<T>(JsonNode? n) => n.Deserialize<T>(Json.Options)!;

    [Fact]
    public void TriggerEffects()
    {
        foreach (var c in Fx["effects"]!.AsArray())
        {
            var a = c!["args"] is { } args ? Ints(args) : [];
            byte[] got = (string)c["kind"]! switch
            {
                "zones" => Effects.Zones(a),
                "smooth" => Effects.Smooth(a[0], a[1]),
                "click" => Effects.Click(a[0], a[1], a[2]),
                "vibrate" => Effects.Vibrate(a[0], a[1], a[2]),
                _ => Effects.ForReaction(From<ReactionEffect>(c["effect"])),
            };
            Assert.Equal(Bytes(c["bytes"]), got);
        }
    }

    [Fact]
    public void OutputReports()
    {
        foreach (var c in Fx["reports"]!.AsArray())
        {
            var lb = Bytes(c!["lb"]);
            var common = (string)c["pick"]! == "trig"
                ? Effects.Common(Bytes(c["l"]), Bytes(c["r"]))
                : Effects.Common(lightbar: (lb[0], lb[1], lb[2]), playerLeds: (byte)(int)c["leds"]!);
            Assert.Equal(Bytes(c["bytes"]), Effects.Report(common, (bool)c["bt"]!, (int)c["seq"]!));
        }
    }

    [Fact]
    public void InputReports()
    {
        foreach (var c in Fx["inputs"]!.AsArray())
        {
            var st = InputState.Parse(Bytes(c!["report"]))!;
            var want = c["state"]!;
            Assert.Equal((int)want["lx"]!, st.LX);
            Assert.Equal((int)want["ry"]!, st.RY);
            Assert.Equal((int)want["l2"]!, st.L2);
            Assert.Equal((int)want["r2"]!, st.R2);
            Assert.Equal(want["buttons"]!.AsArray().Select(x => (string)x!), st.Buttons);
            Assert.Equal(Ints(want["gyro"]), new int[] { st.Gyro.X, st.Gyro.Y, st.Gyro.Z });
            Assert.Equal(Ints(want["accel"]), new int[] { st.Accel.X, st.Accel.Y, st.Accel.Z });
            Assert.Equal(want["touches"]!.AsArray().Select(t => ((int)t!["id"]!, (int)t["x"]!, (int)t["y"]!)),
                         st.Touches.Select(t => (t.Id, t.X, t.Y)));
            Assert.Equal((int)want["battery"]!, st.Battery);
            Assert.Equal((string)want["charging"]!, st.Charging);
        }
    }

    [Fact]
    public void ThrowRemap()
    {
        foreach (var c in Fx["remap"]!.AsArray())
            Assert.Equal((double)c!["v"]!, Effects.Remap((int)c["raw"]!, From<TriggerOutput>(c["o"])), 9);
    }

    [Fact]
    public void LightBar()
    {
        foreach (var c in Fx["light"]!.AsArray())
        {
            var preset = new Preset { Lightbar = (string)c!["preset"]!["lightbar"]! };
            var rgb = Lighting.Color(From<LightingSettings>(c["cfg"]), preset, (int)c["battery"]!, (double)c["t"]!);
            Assert.Equal(Bytes(c["rgb"]), new[] { rgb.R, rgb.G, rgb.B });
        }
    }

    [Fact]
    public void GameTelemetry_()
    {
        foreach (var run in Fx["telemetry"]!.AsArray())
        {
            var g = new GameTelemetry();
            int port = (int)run!["port"]!;
            foreach (var s in run["steps"]!.AsArray())
            {
                g.Feed(port, Bytes(s!["pkt"]));
                Assert.Equal((int?)s["gear"], g.Gear);
                Assert.Equal((bool)s["abs"]!, g.Abs);
                Assert.Equal((bool)s["spin"]!, g.Wheelspin);
                Assert.Equal((bool)s["red"]!, g.Redline);
                Assert.Equal((double)s["speed"]!, g.Speed, 4);
                Assert.Equal((bool)s["changed"]!, g.TakeGearChange());
            }
        }
    }

    [Fact]
    public void BuiltinPresetsMatch()
    {
        var python = Fx["builtins"]!.AsArray();
        var ours = JsonSerializer.SerializeToNode(Builtins.All(), Json.Options)!.AsArray();
        Assert.Equal(python.Count, ours.Count);
        for (int i = 0; i < python.Count; i++)
        {
            // reactions_on is new in the editor; the Python presets leave it out (default on).
            foreach (var side in new[] { "l2", "r2" })
                ours[i]!["triggers"]![side]!.AsObject().Remove("reactions_on");
            Assert.True(JsonNode.DeepEquals(python[i], ours[i]), $"{python[i]!["id"]}:\n{python[i]}\n---\n{ours[i]}");
        }
    }
}
