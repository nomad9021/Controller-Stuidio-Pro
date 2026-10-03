using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ControllerStudio;

/// <summary>A virtual Xbox 360 controller through the ViGEmBus driver. Games see this instead of
/// (or as well as) the real controller; rumble they send to it is passed back to the DualSense.</summary>
public sealed class VirtualPad : IDisposable
{
    static readonly Dictionary<string, Xbox360Button> Buttons = new()
    {
        ["cross"] = Xbox360Button.A, ["circle"] = Xbox360Button.B,
        ["square"] = Xbox360Button.X, ["triangle"] = Xbox360Button.Y,
        ["l1"] = Xbox360Button.LeftShoulder, ["r1"] = Xbox360Button.RightShoulder,
        ["create"] = Xbox360Button.Back, ["options"] = Xbox360Button.Start, ["ps"] = Xbox360Button.Guide,
        ["l3"] = Xbox360Button.LeftThumb, ["r3"] = Xbox360Button.RightThumb,
        ["up"] = Xbox360Button.Up, ["down"] = Xbox360Button.Down,
        ["left"] = Xbox360Button.Left, ["right"] = Xbox360Button.Right,
    };

    static long _checkedAt;
    static bool _available;

    readonly ViGEmClient _client;
    readonly IXbox360Controller _pad;
    readonly EventWaitHandle _wake;
    readonly Lock _lock = new();
    (double Strong, double Weak)? _rumble;
    (ushort, short, short, short, short, byte, byte)? _last;

    /// <summary>True when the ViGEmBus driver is installed (checked at most every 5 s).</summary>
    public static bool Available
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            if (_available || (_checkedAt != 0 && Stopwatch.GetElapsedTime(_checkedAt).TotalSeconds < 5))
                return _available;
            try
            {
                using var c = new ViGEmClient();
                _available = true;
            }
            catch (Exception)
            {
                _available = false;
            }
            _checkedAt = Stopwatch.GetTimestamp();
            return _available;
        }
    }

    public VirtualPad(EventWaitHandle wake)
    {
        _wake = wake;
        try
        {
            _client = new ViGEmClient();
        }
        catch (Exception e)
        {
            throw new IOException("Couldn't create the virtual controller: the ViGEmBus driver isn't installed.", e);
        }
        _pad = _client.CreateXbox360Controller();
        _pad.AutoSubmitReport = false;
        _pad.FeedbackReceived += (_, e) =>
        {
            lock (_lock)
                _rumble = (e.LargeMotor / 255.0, e.SmallMotor / 255.0);
            _wake.Set();
        };
        _pad.Connect();
    }

    /// <summary>Mirror the real controller, with remapped triggers and extra buttons.</summary>
    public void Update(InputState st, double l2, double r2, IReadOnlyDictionary<string, string> map)
    {
        var pressed = new HashSet<string>(st.Buttons);
        foreach (var (src, dst) in map)
            if (!string.IsNullOrEmpty(dst) && pressed.Contains(src))
                pressed.Add(dst);
        if (pressed.Contains("touchpad"))
            pressed.Add("create");
        ushort buttons = 0;
        foreach (var (name, b) in Buttons)
            if (pressed.Contains(name))
                buttons |= (ushort)b.Value;
        static short Stick(byte v) => (short)Math.Clamp((v - 128) * 258, -32768, 32767);
        var state = (buttons, Stick(st.LX), (short)(-1 - Stick(st.LY)), Stick(st.RX), (short)(-1 - Stick(st.RY)),  // XInput Y points up
                     (byte)(l2 * 255), (byte)(r2 * 255));
        if (state == _last)
            return;
        _last = state;
        _pad.SetButtonsFull(state.buttons);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, state.Item2);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY, state.Item3);
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, state.Item4);
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, state.Item5);
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, state.Item6);
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, state.Item7);
        _pad.SubmitReport();
    }

    /// <summary>The (strong, weak) rumble the game most recently asked for, once.</summary>
    public (double Strong, double Weak)? TakeRumble()
    {
        lock (_lock)
        {
            var r = _rumble;
            _rumble = null;
            return r;
        }
    }

    public void Dispose()
    {
        try { _pad.Disconnect(); } catch (Exception) { }
        _client.Dispose();
    }
}
