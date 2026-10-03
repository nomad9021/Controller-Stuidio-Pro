using System.Buffers.Binary;
using System.Diagnostics;

namespace ControllerStudio;

/// <summary>Live game data over UDP, normalised to what trigger reactions need:
/// gear changes, wheel lock (ABS), wheelspin and the rev limiter.
///
///   Forza Horizon 4/5, Forza Motorsport 7 / 2023  - "Data Out", port 5300
///   F1 22 / 23 / 24 / 25                           - UDP telemetry, port 20777
///   DiRT Rally 2.0, DiRT 4, GRID (Codemasters)     - extradata=3, port 20777
///   BeamNG.drive, Live for Speed                   - OutGauge, port 4444
/// </summary>
public sealed class GameTelemetry
{
    public static readonly IReadOnlyDictionary<int, string> Ports = new Dictionary<int, string>
    {
        [5300] = "Forza", [20777] = "Codemasters / F1", [4444] = "OutGauge",
    };

    long _last;
    bool _gearChanged;
    (float RL, float RR, float FL, float FR) _f1Slip;

    public string? Source { get; private set; }
    public int? Gear { get; private set; }
    public bool Abs { get; private set; }
    public bool Wheelspin { get; private set; }
    public bool Redline { get; private set; }
    /// <summary>Metres per second.</summary>
    public double Speed { get; private set; }

    public bool Active => _last != 0 && Stopwatch.GetElapsedTime(_last).TotalSeconds < 1.0;

    void Update(string source, int gear, double speed, bool abs, bool spin, bool red)
    {
        _last = Stopwatch.GetTimestamp();
        Source = source;
        if (Gear is { } g && gear != g && gear != 0)
            _gearChanged = true;
        Gear = gear;
        Speed = speed;
        Abs = abs;
        Wheelspin = spin;
        Redline = red;
    }

    public bool TakeGearChange()
    {
        var changed = _gearChanged;
        _gearChanged = false;
        return changed;
    }

    public void Feed(int port, ReadOnlySpan<byte> pkt)
    {
        try
        {
            switch (port)
            {
                case 5300: Forza(pkt); break;
                case 20777 when pkt.Length is 256 or 264: Codemasters(pkt); break;
                case 20777: F1(pkt); break;
                case 4444: OutGauge(pkt); break;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // a short packet
        }
    }

    static float F(ReadOnlySpan<byte> p, int o) => BinaryPrimitives.ReadSingleLittleEndian(p[o..]);

    // Forza "Data Out" (sled 232 / dash 311 / Horizon 324 / FM2023 331)
    void Forza(ReadOnlySpan<byte> p)
    {
        if (p.Length < 311)
            return;
        if (BinaryPrimitives.ReadInt32LittleEndian(p) == 0)  // IsRaceOn = 0: menus / paused
        {
            _last = 0;
            return;
        }
        int dash = p.Length == 324 ? 244 : 232;  // Horizon inserts 12 bytes
        float maxRpm = F(p, 8), rpm = F(p, 16);
        float[] slip = [F(p, 84), F(p, 88), F(p, 92), F(p, 96)];  // FL FR RL RR
        float speed = F(p, dash + 12);
        byte accel = p[dash + 71], brake = p[dash + 72], gear = p[dash + 75];
        Update("Forza", gear, speed,
               brake > 100 && speed > 3 && slip.Max(Math.Abs) > 1.0,
               accel > 100 && Math.Max(Math.Abs(slip[2]), Math.Abs(slip[3])) > 1.2,
               maxRpm > 0 && rpm > 0.97 * maxRpm);
    }

    // Codemasters legacy "extradata=3": 64-66 little-endian floats
    void Codemasters(ReadOnlySpan<byte> p)
    {
        float speed = F(p, 28);
        float rl = F(p, 100), rr = F(p, 104), fl = F(p, 108), fr = F(p, 112);
        float throttle = F(p, 116), brake = F(p, 124);
        int gear = (int)Math.Round(F(p, 132), MidpointRounding.ToEven);
        float rpm = F(p, 148) * 10, maxRpm = F(p, 252) * 10;
        Update("DiRT / GRID", gear, speed,
               brake > 0.4 && speed > 3 && Math.Min(fl, fr) < speed * 0.75,
               throttle > 0.4 && speed > 1 && Math.Max(rl, rr) > speed * 1.25 + 1,
               maxRpm > 0 && rpm > 0.96 * maxRpm);
    }

    // F1 22-25: car telemetry + wheel slip from the motion packets
    void F1(ReadOnlySpan<byte> p)
    {
        int fmt = BinaryPrimitives.ReadUInt16LittleEndian(p);
        int h, pid, player;
        if (fmt == 2022) (h, pid, player) = (24, p[5], p[22]);
        else if (fmt >= 2023) (h, pid, player) = (29, p[6], p[27]);
        else return;
        if (pid == 6)  // car telemetry, 60 bytes per car
        {
            int o = h + player * 60;
            int kmh = BinaryPrimitives.ReadUInt16LittleEndian(p[o..]);
            float throttle = F(p, o + 2), brake = F(p, o + 10);
            int gear = (sbyte)p[o + 15], revPct = p[o + 19];
            var s = _f1Slip;
            Update($"F1 {fmt}", gear, kmh / 3.6,
                   brake > 0.5 && kmh > 10 && Math.Max(Math.Abs(s.FL), Math.Abs(s.FR)) > 0.25,
                   throttle > 0.5 && Math.Max(Math.Abs(s.RL), Math.Abs(s.RR)) > 0.25,
                   revPct >= 92);
        }
        else if ((fmt >= 2023 && pid == 13) || (fmt == 2022 && pid == 0))
        {
            int off = fmt >= 2023 ? h + 64 : h + 22 * 60 + 64;
            _f1Slip = (F(p, off), F(p, off + 4), F(p, off + 8), F(p, off + 12));
        }
    }

    // OutGauge (BeamNG.drive, Live for Speed)
    void OutGauge(ReadOnlySpan<byte> p)
    {
        if (p.Length < 92)
            return;
        int gear = p[10] - 1;  // 0=R, 1=N, 2=1st -> -1/0/1...
        float speed = F(p, 12);
        uint lights = BinaryPrimitives.ReadUInt32LittleEndian(p[44..]);
        Update("BeamNG / LFS", gear, speed,
               (lights & (1 << 10)) != 0,   // DL_ABS
               (lights & (1 << 4)) != 0,    // DL_TC (traction control = wheelspin)
               (lights & (1 << 0)) != 0);   // DL_SHIFT
    }
}
