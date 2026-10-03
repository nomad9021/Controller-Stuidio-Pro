using System.Buffers.Binary;

namespace ControllerStudio;

public readonly record struct Touch(int Id, int X, int Y);

/// <summary>One decoded input report.</summary>
public sealed record InputState(
    byte LX, byte LY, byte RX, byte RY, byte L2, byte R2,
    IReadOnlyList<string> Buttons,
    (short X, short Y, short Z) Gyro,
    (short X, short Y, short Z) Accel,
    IReadOnlyList<Touch> Touches,
    int Battery,
    string Charging)
{
    public byte Trigger(string side) => side == "l2" ? L2 : R2;

    // name -> (byte index into buttons[0..3], bit)
    public static readonly (string Name, int Index, int Bit)[] ButtonBits =
    [
        ("square", 0, 0x10), ("cross", 0, 0x20), ("circle", 0, 0x40), ("triangle", 0, 0x80),
        ("l1", 1, 0x01), ("r1", 1, 0x02), ("l2", 1, 0x04), ("r2", 1, 0x08),
        ("create", 1, 0x10), ("options", 1, 0x20), ("l3", 1, 0x40), ("r3", 1, 0x80),
        ("ps", 2, 0x01), ("touchpad", 2, 0x02), ("mute", 2, 0x04),
        ("fn_left", 2, 0x10), ("fn_right", 2, 0x20), ("paddle_left", 2, 0x40), ("paddle_right", 2, 0x80),
    ];

    static readonly string[][] Dpad =
    [
        ["up"], ["up", "right"], ["right"], ["down", "right"], ["down"], ["down", "left"], ["left"], ["up", "left"],
    ];

    static short S(ReadOnlySpan<byte> c, int o) => BinaryPrimitives.ReadInt16LittleEndian(c[o..]);

    /// <summary>Decode a full input report (USB 0x01 or Bluetooth 0x31); null for anything else.</summary>
    public static InputState? Parse(ReadOnlySpan<byte> r)
    {
        ReadOnlySpan<byte> c;
        if (r.Length >= 66 && r[0] == 0x31)
            c = r[2..];
        else if (r.Length >= 64 && r[0] == 0x01)
            c = r[1..];
        else
            return null;
        var btn = c.Slice(7, 4);
        var pressed = new List<string>();
        foreach (var (name, i, bit) in ButtonBits)
            if ((btn[i] & bit) != 0)
                pressed.Add(name);
        int hat = btn[0] & 0x0F;
        if (hat < 8)
            pressed.AddRange(Dpad[hat]);
        var touches = new List<Touch>();
        foreach (var o in new[] { 32, 36 })
        {
            var p = c.Slice(o, 4);
            if ((p[0] & 0x80) == 0)
                touches.Add(new Touch(p[0] & 0x7F, p[1] | (p[2] & 0x0F) << 8, p[2] >> 4 | p[3] << 4));
        }
        int status = c[52];
        var gyro = (S(c, 15), S(c, 17), S(c, 19));
        var accel = (S(c, 21), S(c, 23), S(c, 25));
        return new InputState(
            c[0], c[1], c[2], c[3], c[4], c[5], pressed, gyro, accel,
            touches,
            Math.Min((status & 0x0F) * 10 + 5, 100),
            (status >> 4) switch { 0 => "discharging", 1 => "charging", 2 => "full", _ => "unknown" });
    }
}

public static class Lighting
{
    /// <summary>The light bar colour right now.</summary>
    public static (byte R, byte G, byte B) Color(LightingSettings cfg, Preset? preset, int? battery, double t)
    {
        if (cfg.Mode == "off")
            return (0, 0, 0);
        var hex = cfg.Mode == "preset" ? preset?.Lightbar ?? "#ffffff" : cfg.Color;
        var (r, g, b) = ParseHex(hex);
        double level = cfg.Brightness / 100.0;
        switch (cfg.Effect)
        {
            case "breathe":
                level *= 0.15 + 0.85 * (0.5 - 0.5 * Math.Cos(t * cfg.Speed * 0.6));
                break;
            case "rainbow":
                (r, g, b) = Hsv(t * cfg.Speed * 0.03 % 1, 1, 1);
                break;
            case "battery":
                (r, g, b) = Hsv((battery ?? 0) / 100.0 * 0.33, 1, 1);
                break;
        }
        static byte C(double v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v, MidpointRounding.ToEven)));
        return (C(r * level * 255), C(g * level * 255), C(b * level * 255));
    }

    public static (double R, double G, double B) ParseHex(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#')
            return (1, 1, 1);
        try
        {
            return (Convert.ToInt32(hex[1..3], 16) / 255.0, Convert.ToInt32(hex[3..5], 16) / 255.0,
                    Convert.ToInt32(hex[5..7], 16) / 255.0);
        }
        catch (FormatException)
        {
            return (1, 1, 1);
        }
    }

    /// <summary>Python's colorsys.hsv_to_rgb.</summary>
    public static (double, double, double) Hsv(double h, double s, double v)
    {
        if (s == 0) return (v, v, v);
        int i = (int)(h * 6.0);
        double f = h * 6.0 - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return (i % 6) switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
    }

    public static string Hex((byte R, byte G, byte B) c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
}
