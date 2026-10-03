using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ControllerStudioPro;

/// <summary>Bits of Windows that WPF doesn't wrap.</summary>
static partial class Native
{
    // ---- the standard Windows colour picker

    [StructLayout(LayoutKind.Sequential)]
    struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChooseColor(ref CHOOSECOLOR cc);

    static readonly IntPtr CustomColors = Marshal.AllocHGlobal(16 * 4);

    /// <summary>"#rrggbb", or null if cancelled.</summary>
    public static string? PickColor(Window owner, string current)
    {
        var (r, g, b) = ControllerStudio.Lighting.ParseHex(current);
        var cc = new CHOOSECOLOR
        {
            lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
            hwndOwner = new WindowInteropHelper(owner).Handle,
            rgbResult = (int)(r * 255) | (int)(g * 255) << 8 | (int)(b * 255) << 16,
            lpCustColors = CustomColors,
            Flags = 0x00000001 | 0x00000002,  // CC_RGBINIT | CC_FULLOPEN
        };
        if (!ChooseColor(ref cc))
            return null;
        int c = cc.rgbResult;
        return $"#{c & 0xFF:x2}{c >> 8 & 0xFF:x2}{c >> 16 & 0xFF:x2}";
    }

    // ---- start with Windows

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Controller Studio Pro";

    static string RunCommand => $"\"{Environment.ProcessPath}\" --background";

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(RunName, RunCommand);
            else
                key.DeleteValue(RunName, false);
        }
    }

    /// <summary>Point an existing start-up entry at this copy of the app (it may have moved).</summary>
    public static void RefreshStartup()
    {
        if (StartsWithWindows)
            StartsWithWindows = true;
    }

    // ---- Moonlight: keep its SDL away from the real controller so it only sees the virtual one

    static readonly Dictionary<string, string> MoonlightEnv = new()
    {
        ["SDL_GAMECONTROLLER_IGNORE_DEVICES"] = "0x054c/0x0ce6,0x054c/0x0df2",
        ["SDL_JOYSTICK_HIDAPI_PS5"] = "0",
    };

    public static bool MoonlightHidden
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment");
            return key?.GetValue(MoonlightEnv.Keys.First()) is not null;
        }
    }

    public static void SetMoonlightHidden(bool hide)
    {
        using (var key = Registry.CurrentUser.CreateSubKey("Environment"))
            foreach (var (k, v) in MoonlightEnv)
                if (hide)
                    key.SetValue(k, v);
                else
                    key.DeleteValue(k, false);
        // Tell Explorer, so Moonlight started from the Start menu sees the change.
        SendMessageTimeout(0xFFFF, 0x001A, 0, "Environment", 0x0002, 2000, out _);
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
                                                     uint flags, uint timeout, out UIntPtr result);

    static IntPtr SendMessageTimeout(int hWnd, uint msg, int wParam, string lParam, uint flags, uint timeout, out UIntPtr result) =>
        SendMessageTimeout(hWnd, msg, (UIntPtr)wParam, lParam, flags, timeout, out result);

    // ---- this machine's addresses, for the game data setup instructions

    public static List<string> LocalAddresses()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = a.Address;
                    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;
                    var s = ip.ToString();
                    if (!s.StartsWith("127.") && !s.StartsWith("169.254.") && !ips.Contains(s))
                        ips.Add(s);
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return ips;
    }
}
