using System.Collections.Concurrent;
using HidSharp;

namespace ControllerStudio;

public sealed record DeviceInfo(string Model, bool Bluetooth, string Address);

/// <summary>A connected DualSense, read on its own thread. Reports are queued and
/// <c>wake</c> is set for each one so the engine can wait on everything at once.</summary>
public sealed class DualSense : IDisposable
{
    public const int Sony = 0x054C;
    public static readonly IReadOnlyDictionary<int, string> Models = new Dictionary<int, string>
    {
        [0x0CE6] = "DualSense", [0x0DF2] = "DualSense Edge",
    };
    const string BtHidUuid = "00001124-0000-1000-8000-00805f9b34fb";  // in Bluetooth HID device paths

    readonly HidStream _stream;
    readonly int _outLength;
    readonly Thread _reader;
    readonly EventWaitHandle _wake;
    readonly Lock _writeLock = new();
    readonly ConcurrentQueue<byte[]> _reports = new();
    Timer? _rumbleTimer;
    int _seq;
    volatile bool _gone;

    public DeviceInfo Info { get; private set; }
    public bool Gone => _gone;

    public static HidDevice? Find() =>
        DeviceList.Local.GetHidDevices(Sony).FirstOrDefault(d => Models.ContainsKey(d.ProductID));

    public DualSense(HidDevice dev, EventWaitHandle wake)
    {
        _wake = wake;
        if (!dev.TryOpen(out _stream))
            throw new IOException("The controller is in use by another program.");
        _stream.ReadTimeout = 250;
        _outLength = OperatingSystem.IsWindows() ? dev.GetMaxOutputReportLength() : 0;
        string address = "";
        try { address = dev.GetSerialNumber(); } catch (Exception e) when (e is IOException or NotSupportedException) { }
        Info = new DeviceInfo(Models[dev.ProductID], dev.DevicePath.Contains(BtHidUuid, StringComparison.OrdinalIgnoreCase),
                              address);

        if (Info.Bluetooth)
        {
            // Reading the calibration report switches Bluetooth to full 0x31 input reports.
            try
            {
                var feature = new byte[Math.Max(41, dev.GetMaxFeatureReportLength())];
                feature[0] = 0x05;
                _stream.GetFeature(feature);
            }
            catch (Exception e) when (e is IOException or TimeoutException) { }
        }
        // Windows has no driver doing setup: take over the light bar and fade out the default blue.
        var common = new byte[47];
        common[38] = 0x02;
        common[41] = 0x02;
        Write(common);

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "DualSense reader" };
        _reader.Start();
    }

    void ReadLoop()
    {
        var buf = new byte[128];
        while (!_gone)
        {
            try
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n <= 0)
                    continue;
                var r = buf.AsSpan(0, n).ToArray();
                // The first full report tells us for sure how it is connected.
                if (r[0] == 0x31 && n >= 66 && !Info.Bluetooth)
                    Info = Info with { Bluetooth = true };
                else if (r[0] == 0x01 && n >= 64 && Info.Bluetooth)
                    Info = Info with { Bluetooth = false };
                _reports.Enqueue(r);
                while (_reports.Count > 64)
                    _reports.TryDequeue(out _);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _gone = true;
            }
            _wake.Set();
        }
    }

    public bool TryRead(out byte[] report) => _reports.TryDequeue(out report!);

    void Write(byte[] common)
    {
        lock (_writeLock)
        {
            var r = Effects.Report(common, Info.Bluetooth, _seq++);
            if (_outLength > r.Length)  // Windows wants the longest output report's length
                Array.Resize(ref r, _outLength);
            _stream.Write(r);
        }
    }

    public void Send(byte[]? left = null, byte[]? right = null, (byte, byte, byte)? lightbar = null, byte? playerLeds = null) =>
        Write(Effects.Common(left, right, lightbar, playerLeds));

    /// <summary>Play (or with both 0, stop) rumble through the motor bytes; with ms it stops by itself.</summary>
    public void Rumble(double strong, double weak, int ms = 0)
    {
        _rumbleTimer?.Dispose();
        _rumbleTimer = null;
        var common = new byte[47];
        common[0] = 0x01 | 0x02;  // classic rumble instead of haptics
        common[2] = (byte)Math.Clamp((int)(weak * 255), 0, 255);    // right, small motor
        common[3] = (byte)Math.Clamp((int)(strong * 255), 0, 255);  // left, big motor
        Write(common);
        if (ms > 0 && (strong > 0 || weak > 0))
            _rumbleTimer = new Timer(_ =>
            {
                try { Rumble(0, 0); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }, null, ms, Timeout.Infinite);
    }

    public void Dispose()
    {
        _rumbleTimer?.Dispose();
        try
        {
            Rumble(0, 0);
            Send(Effects.Off, Effects.Off);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or TimeoutException) { }
        _gone = true;
        _reader.Join(1000);
        _stream.Dispose();
    }
}
