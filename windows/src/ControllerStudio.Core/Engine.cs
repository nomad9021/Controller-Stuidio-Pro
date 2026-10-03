using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ControllerStudio;

/// <summary>What the engine is doing right now, for the window.</summary>
public sealed record EngineSnapshot(
    DeviceInfo? Device,
    InputState? Input,
    double OutL2, double OutR2,
    int Rate,
    (byte R, byte G, byte B)? Light,
    byte PlayerLeds,
    bool Enabled,
    string? FiringL2, string? FiringR2,
    bool VirtualActive, string? VirtualError,
    bool TelemetryActive, string? TelemetrySource, int? Gear, int? SpeedKmh,
    int Rumble);

/// <summary>Talks to the DualSense: parses input, drives adaptive triggers, lighting and rumble,
/// runs each preset's reactions, and (optionally) feeds a remapped virtual controller to games.
/// Runs on its own thread; the public methods can be called from any thread.</summary>
public sealed class Engine : IDisposable
{
    static readonly HashSet<string> Instant = ["buttons", "gear", "slam"];  // fire once for duration_ms
    static readonly HashSet<string> GameEvents = ["gear", "abs", "wheelspin", "redline"];

    readonly Lock _lock = new();
    readonly AutoResetEvent _wake = new(false);
    readonly GameTelemetry _game = new();
    readonly Thread _thread;
    volatile bool _stop;

    Preset? _preset;
    AppSettings _settings = new();
    bool _enabled = true;
    (byte[] Left, byte[] Right, long Until)? _override;
    bool _refresh = true;

    DualSense? _dev;
    InputState? _state;
    double _outL2, _outR2;
    int _rate;
    ((byte, byte, byte) Color, byte Leds)? _light;
    string? _firingL2, _firingR2;
    string? _vpadError;
    bool _vpadActive;
    (double Strong, double Weak) _rumbleLevel;

    /// <summary>Something went wrong talking to the controller (raised on the engine thread).</summary>
    public event Action<string>? Problem;

    public Engine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Controller Studio engine" };
    }

    public void Start() => _thread.Start();

    // ---- public API (any thread)

    public void SetPreset(Preset preset)
    {
        var copy = preset.Clone<Preset>();
        lock (_lock)
        {
            _preset = copy;
            _refresh = true;
        }
        _wake.Set();
    }

    public void SetSettings(AppSettings settings)
    {
        var copy = settings.Clone<AppSettings>();
        lock (_lock)
        {
            _settings = copy;
            _refresh = true;
        }
        _wake.Set();
    }

    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
        set
        {
            lock (_lock) _enabled = value;
            _wake.Set();
        }
    }

    /// <summary>Play effects on the triggers for a while, ahead of the preset (Feel It and the Trigger Lab).
    /// Null for both stops it.</summary>
    public void SetOverride(byte[]? left, byte[]? right, double seconds)
    {
        lock (_lock)
            _override = left is null && right is null ? null
                : (left ?? Effects.Off, right ?? Effects.Off,
                   Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency));
        _wake.Set();
    }

    public void TestFeel(string side, Feel feel, double seconds) => Test(side, Effects.ForFeel(feel), seconds);

    public void TestReaction(string side, ReactionEffect effect, bool hold, double seconds) =>
        Test(side, Effects.ForReaction(effect),
             hold ? Math.Max(seconds, effect.DurationMs / 1000.0) : effect.DurationMs / 1000.0);

    void Test(string side, byte[] fx, double seconds)
    {
        if (side == "l2") SetOverride(fx, Effects.Off, seconds);
        else if (side == "both") SetOverride(fx, fx, seconds);
        else SetOverride(Effects.Off, fx, seconds);
    }

    public void StopTest() => SetOverride(null, null, 0);

    public void Rumble(double strong, double weak, int ms)
    {
        try { _dev?.Rumble(strong, weak, ms); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public EngineSnapshot Snapshot()
    {
        lock (_lock)
        {
            var dev = _dev;
            bool tele = _game.Active;
            return new EngineSnapshot(
                dev?.Info, _state, Math.Round(_outL2 * 100, 1), Math.Round(_outR2 * 100, 1), _rate,
                dev != null ? _light?.Color : null, dev != null ? _light?.Leds ?? 0 : (byte)0,
                _enabled, _firingL2, _firingR2, _vpadActive, _vpadError,
                tele, tele ? _game.Source : null, _game.Gear,
                tele ? (int)Math.Round(_game.Speed * 3.6) : null,
                (int)Math.Round(Math.Max(_rumbleLevel.Strong, _rumbleLevel.Weak) * 100));
        }
    }

    // ---- engine thread

    static double Seconds(long since, long now) => (now - since) / (double)Stopwatch.Frequency;

    void Run()
    {
        var socks = new List<(int Port, Socket Sock)>();
        foreach (var port in GameTelemetry.Ports.Keys)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                if (OperatingSystem.IsWindows())
                    sock.ExclusiveAddressUse = true;  // don't let another program split the packets
                sock.Bind(new IPEndPoint(IPAddress.Any, port));
                sock.Blocking = false;
                socks.Add((port, sock));
            }
            catch (SocketException)
            {
                sock.Dispose();
            }
        }
        while (!_stop)
        {
            var hid = DualSense.Find();
            if (hid is null)
            {
                _wake.WaitOne(1000);
                continue;
            }
            DualSense dev;
            try
            {
                dev = new DualSense(hid, _wake);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Problem?.Invoke("Couldn't open the controller: " + e.Message);
                Thread.Sleep(2000);
                continue;
            }
            lock (_lock)
            {
                _dev = dev;
                _refresh = true;
            }
            try
            {
                Loop(dev, socks);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or TimeoutException)
            {
                // Usually unplugged or switched off; worth a line in the log in case it isn't.
                Problem?.Invoke($"Controller connection ended: {e.GetType().Name}: {e.Message}");
            }
            catch (Exception e)
            {
                Problem?.Invoke(e.ToString());
            }
            finally
            {
                lock (_lock)
                {
                    _dev = null;
                    _state = null;
                    _vpadActive = false;
                }
                dev.Dispose();
            }
            if (!_stop)
                Thread.Sleep(1000);
        }
        foreach (var (_, s) in socks)
            s.Dispose();
    }

    void Loop(DualSense dev, List<(int Port, Socket Sock)> socks)
    {
        VirtualPad? vpad = null;
        var prevButtons = new HashSet<string>();
        var hist = new Dictionary<string, List<(long T, int V)>> { ["l2"] = [], ["r2"] = [] };
        var until = new Dictionary<string, long>();  // reaction -> when it stops
        long? comboSince = null;
        (byte[] L, byte[] R)? sent = null;
        long lastSend = 0, lastLight = 0, countT = Stopwatch.GetTimestamp(), t0 = countT;
        ((byte, byte, byte), byte)? sentLight = null;
        int count = 0;
        var buf = new byte[2048];

        try
        {
            while (!_stop)
            {
                if (dev.Gone)
                    throw new IOException("controller disconnected");
                long now = Stopwatch.GetTimestamp();
                Preset preset;
                AppSettings settings;
                bool enabled, refresh, hasPreset;
                (byte[] Left, byte[] Right, long Until)? ov;
                lock (_lock)
                {
                    hasPreset = _preset != null;
                    preset = _preset ?? new Preset();
                    settings = _settings;
                    enabled = _enabled;
                    ov = _override;
                    refresh = _refresh;
                    _refresh = false;
                }
                bool wantVpad = settings.Output.Virtual;

                // Bring the virtual controller up or down to match the setting.
                if (wantVpad && vpad is null)
                {
                    try
                    {
                        vpad = new VirtualPad(_wake);
                        lock (_lock) (_vpadActive, _vpadError) = (true, null);
                    }
                    catch (Exception e)
                    {
                        lock (_lock) _vpadError = e.Message;
                        // Turn the setting off for this run so it isn't retried on every pass.
                        lock (_lock) _settings.Output.Virtual = false;
                    }
                }
                else if (!wantVpad && vpad is not null)
                {
                    vpad.Dispose();
                    vpad = null;
                    lock (_lock) _vpadActive = false;
                }

                _wake.WaitOne(TimeSpan.FromSeconds(1 / 120.0));
                now = Stopwatch.GetTimestamp();

                foreach (var (port, sock) in socks)
                {
                    try
                    {
                        while (sock.Available > 0)
                        {
                            int n = sock.Receive(buf);
                            _game.Feed(port, buf.AsSpan(0, n));
                        }
                    }
                    catch (SocketException) { }
                }

                if (vpad?.TakeRumble() is { } rumble)
                {
                    _rumbleLevel = rumble;
                    try { dev.Rumble(rumble.Strong, rumble.Weak); }
                    catch (IOException) { }
                }

                var newly = new HashSet<string>();
                while (dev.TryRead(out var r))
                {
                    var st = InputState.Parse(r);
                    if (st is null)
                        continue;
                    count++;
                    var buttons = st.Buttons.ToHashSet();
                    newly.UnionWith(buttons.Except(prevButtons));
                    prevButtons = buttons;
                    hist["l2"].Add((now, st.L2));
                    hist["r2"].Add((now, st.R2));
                    double oL2 = Effects.Remap(st.L2, preset.Triggers.L2.Output);
                    double oR2 = Effects.Remap(st.R2, preset.Triggers.R2.Output);
                    lock (_lock)
                    {
                        _state = st;
                        _outL2 = oL2;
                        _outR2 = oR2;
                    }
                    vpad?.Update(st, oL2, oR2, settings.Output.Map);
                }

                if (Seconds(countT, now) >= 1.0)
                {
                    lock (_lock) _rate = (int)Math.Round(count / Seconds(countT, now));
                    (count, countT) = (0, now);
                }

                var state = _state;

                // Hold L3 + R3 for a second to toggle effects.
                if (state != null && state.Buttons.Contains("l3") && state.Buttons.Contains("r3"))
                {
                    comboSince ??= now;
                    if (comboSince != long.MaxValue && Seconds(comboSince.Value, now) > 1.0)
                    {
                        lock (_lock) _enabled = enabled = !enabled;
                        comboSince = long.MaxValue;
                    }
                }
                else
                    comboSince = null;

                foreach (var h in hist.Values)
                    h.RemoveAll(e => Seconds(e.T, now) >= 0.5);
                bool telemetry = preset.Telemetry != "none" && _game.Active;
                bool gearNow = _game.TakeGearChange();

                var effects = new Dictionary<string, byte[]>();
                var firing = new Dictionary<string, string?>();
                foreach (var side in new[] { "l2", "r2" })
                {
                    var tcfg = preset.Triggers[side];
                    var fx = Effects.ForFeel(tcfg.Feel);
                    int pull = state?.Trigger(side) ?? 0;
                    firing[side] = null;
                    if (tcfg.ReactionsOn)
                        foreach (var r in tcfg.Reactions)
                        {
                            if (!r.Enabled)
                                continue;
                            if (Fires(r, pull, hist[side], newly, telemetry, gearNow, now))
                                until[r.Id] = now + (long)((Instant.Contains(r.When) ? r.Effect.DurationMs / 1000.0 : 0.05)
                                                           * Stopwatch.Frequency);
                            if (now < until.GetValueOrDefault(r.Id))
                            {
                                var eff = r.Effect;
                                if (r.When == "rumble" && r.Rumble.Scale)
                                {
                                    eff = eff.Clone<ReactionEffect>();
                                    eff.Strength = Math.Max(1, (int)Math.Round(
                                        r.Effect.Strength * Math.Max(_rumbleLevel.Strong, _rumbleLevel.Weak),
                                        MidpointRounding.ToEven));
                                }
                                fx = Effects.ForReaction(eff);
                                firing[side] = r.Id;
                                break;
                            }
                        }
                    effects[side] = fx;
                }
                lock (_lock) (_firingL2, _firingR2) = (firing["l2"], firing["r2"]);

                byte[] left, right;
                if (ov is { } o && now < o.Until)
                    (left, right) = (o.Left, o.Right);
                else if (!enabled || !hasPreset)
                    left = right = Effects.Off;
                else
                {
                    if (ov != null)
                        lock (_lock) _override = null;
                    (left, right) = (effects["l2"], effects["r2"]);
                }

                if (sent is not { } s || !s.L.SequenceEqual(left) || !s.R.SequenceEqual(right)
                    || Seconds(lastSend, now) > 1.0 || refresh)
                {
                    dev.Send(left, right);
                    (sent, lastSend) = ((left, right), now);
                }

                var lcfg = settings.Lighting;
                if (Seconds(lastLight, now) > 1 / 30.0 || refresh)
                {
                    var color = Lighting.Color(lcfg, preset, state?.Battery, Seconds(t0, now));
                    var leds = Effects.PlayerLeds.GetValueOrDefault(lcfg.PlayerLeds, (byte)0x04);
                    if (sentLight != (color, leds) || Seconds(lastLight, now) > 2.0 || refresh)
                    {
                        dev.Send(lightbar: color, playerLeds: leds);
                        sentLight = (color, leds);
                        lock (_lock) _light = (color, leds);
                    }
                    lastLight = now;
                }
            }
        }
        finally
        {
            vpad?.Dispose();
        }
    }

    bool Fires(Reaction r, int pull, List<(long T, int V)> hist, HashSet<string> newly, bool telemetry, bool gearNow, long now)
    {
        switch (r.When)
        {
            case "buttons":
                return r.Buttons.Any(newly.Contains);
            case "slam":
            {
                double window = r.Slam.Within / 1000.0;
                var recent = hist.Where(e => Seconds(e.T, now) <= window).Select(e => e.V).ToList();
                return pull >= r.Slam.To * 2.55 && recent.Count > 0 && recent.Min() <= r.Slam.From * 2.55;
            }
            case "held":
                return pull >= r.Held.Above * 2.55;
            case "rumble":
                return Math.Max(_rumbleLevel.Strong, _rumbleLevel.Weak) * 100 >= r.Rumble.Above;
        }
        if (GameEvents.Contains(r.When) && !telemetry)
            return false;
        return r.When switch
        {
            "gear" => gearNow,
            "abs" => _game.Abs,
            "wheelspin" => _game.Wheelspin,
            "redline" => _game.Redline,
            _ => false,
        };
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive)
            _thread.Join(2000);
    }
}
