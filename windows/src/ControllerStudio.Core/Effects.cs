using System.Buffers.Binary;

namespace ControllerStudio;

/// <summary>DualSense adaptive trigger effects (the 11 bytes per trigger in an output report),
/// output reports, and the trigger throw remap.</summary>
public static class Effects
{
    public static readonly byte[] Off = [0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    static int Clamp(double v, int lo, int hi) => Math.Max(lo, Math.Min(hi, (int)Math.Round(v, MidpointRounding.ToEven)));

    static byte[] Effect(params int[] bytes)
    {
        var b = new byte[11];
        for (int i = 0; i < bytes.Length; i++)
            b[i] = (byte)bytes[i];
        return b;
    }

    /// <summary>Resistance per zone: 10 zones from top to bottom, strength 0 (none)..8.</summary>
    public static byte[] Zones(IReadOnlyList<int> zones)
    {
        int active = 0;
        long force = 0;
        for (int i = 0; i < Math.Min(10, zones.Count); i++)
        {
            int s = Clamp(zones[i], 0, 8);
            if (s > 0)
            {
                active |= 1 << i;
                force |= (long)(s - 1) << (3 * i);
            }
        }
        if (active == 0)
            return Off;
        return Effect(0x21, active & 0xFF, active >> 8,
                      (int)(force & 0xFF), (int)(force >> 8 & 0xFF), (int)(force >> 16 & 0xFF), (int)(force >> 24 & 0xFF));
    }

    /// <summary>Continuous resistance from start% of the pull, force% strong.</summary>
    public static byte[] Smooth(double start, double force) =>
        force <= 0 ? Off : Effect(0x01, Clamp(start * 2.55, 0, 255), Clamp(force * 2.55, 0, 255));

    /// <summary>A wall between zones start (2..7) and end (start+1..8) that breaks with a click.</summary>
    public static byte[] Click(int start, int end, int force)
    {
        start = Clamp(start, 2, 7);
        end = Math.Max(start + 1, Clamp(end, 3, 8));
        int zones = (1 << start) | (1 << end);
        return Effect(0x25, zones & 0xFF, zones >> 8, Clamp(force, 1, 8) - 1);
    }

    /// <summary>Vibrate from startZone (0..9) to the bottom; amplitude 1..8, freq in Hz.</summary>
    public static byte[] Vibrate(int startZone, int amplitude, int freq)
    {
        int active = 0;
        long amp = 0;
        for (int i = Clamp(startZone, 0, 9); i < 10; i++)
        {
            active |= 1 << i;
            amp |= (long)(Clamp(amplitude, 1, 8) - 1) << (3 * i);
        }
        return Effect(0x26, active & 0xFF, active >> 8,
                      (int)(amp & 0xFF), (int)(amp >> 8 & 0xFF), (int)(amp >> 16 & 0xFF), (int)(amp >> 24 & 0xFF),
                      0, 0, Clamp(freq, 1, 255), 0);
    }

    public static byte[] ForFeel(Feel feel) => feel.Type switch
    {
        "zones" => Zones(feel.Zones),
        "smooth" => Smooth(feel.Smooth.Start, feel.Smooth.Force),
        "click" => Click(feel.Click.Start, feel.Click.End, feel.Click.Force),
        "vibrate" => Vibrate(feel.Vibrate.Start, feel.Vibrate.Amplitude, feel.Vibrate.Freq),
        _ => Off,
    };

    public static byte[] ForReaction(ReactionEffect e) => e.Type switch
    {
        "buzz" => Vibrate(e.Start, e.Strength, e.Freq),
        "wall" => Smooth(e.Start * 10, e.Strength / 8.0 * 100),
        _ => Zones([.. Enumerable.Repeat(0, Clamp(e.Start, 0, 9)), .. Enumerable.Repeat(e.Strength, 10)]),
    };

    /// <summary>Physical trigger 0..255 -> output 0..1 using dead zone, throw, curve and max.</summary>
    public static double Remap(double raw, TriggerOutput o) => RemapUnit(raw / 255, o);

    public static double RemapUnit(double x, TriggerOutput o)
    {
        double dz = o.Deadzone / 100.0;
        double full = Math.Max(dz + 0.05, o.FullAt / 100.0);
        if (x <= dz)
            return 0;
        double t = Math.Min(1.0, (x - dz) / (full - dz));
        t = Math.Pow(t, Math.Pow(2, o.Curve / 50.0));
        return t * o.Max / 100;
    }

    // ---------------------------------------------------------------- output reports

    public static readonly Dictionary<string, byte> PlayerLeds = new()
    {
        ["off"] = 0x00, ["center"] = 0x04, ["edges"] = 0x11, ["all"] = 0x1F,
    };

    /// <summary>The 47 bytes shared by USB and Bluetooth output reports.</summary>
    public static byte[] Common(byte[]? left = null, byte[]? right = null, (byte R, byte G, byte B)? lightbar = null,
                                byte? playerLeds = null)
    {
        var c = new byte[47];
        if (right != null)
        {
            c[0] |= 0x04;
            right.CopyTo(c, 10);
        }
        if (left != null)
        {
            c[0] |= 0x08;
            left.CopyTo(c, 21);
        }
        if (lightbar is { } lb)
        {
            c[1] |= 0x04;
            c[44] = lb.R; c[45] = lb.G; c[46] = lb.B;
        }
        if (playerLeds is { } leds)
        {
            c[1] |= 0x10;
            c[43] = leds;
        }
        return c;
    }

    /// <summary>Wrap the 47 common bytes in a USB (0x02) or Bluetooth (0x31) report.</summary>
    public static byte[] Report(byte[] common, bool bluetooth, int seq)
    {
        if (!bluetooth)
        {
            var usb = new byte[48];
            usb[0] = 0x02;
            common.CopyTo(usb, 1);
            return usb;
        }
        var r = new byte[78];
        r[0] = 0x31;
        r[1] = (byte)((seq & 0x0F) << 4);
        r[2] = 0x10;
        common.CopyTo(r, 3);
        uint crc = Crc32.Compute([0xA2], r.AsSpan(0, 74));
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(74), crc);
        return r;
    }
}

/// <summary>zlib's CRC-32, which the DualSense uses to check Bluetooth reports.</summary>
public static class Crc32
{
    static readonly uint[] Table = Build();

    static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in prefix) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
