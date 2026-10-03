using System.IO;
using System.Windows;
using System.Windows.Threading;
using ControllerStudio;

namespace ControllerStudioPro;

/// <summary>One copy runs per user. It keeps the engine going from the notification area, so
/// trigger effects and lighting stay on with the window closed; the window opens on demand.</summary>
public partial class App : Application
{
    const string MutexName = @"Local\ControllerStudioPro";
    const string ShowEventName = @"Local\ControllerStudioPro.Show";

    public static Engine Engine { get; private set; } = null!;
    public static PresetStore Store { get; private set; } = null!;
    public static new App Current => (App)Application.Current;

    public static string LogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                "ControllerStudioPro");

    /// <summary>Raised on the UI thread when presets or the active preset change outside the window.</summary>
    public event EventHandler? PresetsChanged;

    Mutex? _single;
    EventWaitHandle? _showSignal;
    Tray? _tray;
    MainWindow? _window;
    bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject.ToString());

        _single = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: bring that copy's window up instead.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true, Name = "Show signal" }.Start();

        Store = new PresetStore();
        Engine = new Engine();
        Engine.Problem += msg => Log(msg, "engine.log");
        Engine.SetPreset(Store.Active());
        Engine.SetSettings(Store.Settings);
        Engine.Start();
        try { Native.RefreshStartup(); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }

        _tray = new Tray(this);
        if (!e.Args.Contains("--background"))
            ShowWindow();
    }

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>The window was closed: keep running in the notification area, or quit.</summary>
    public void WindowClosed()
    {
        if (_quitting)
            return;
        var w = Store.Settings.Windows;
        if (!w.RunInBackground)
        {
            Quit();
            return;
        }
        if (!w.ToldAboutTray)
        {
            _tray?.Tell("Controller Studio Pro is still running",
                        "Trigger effects and lighting stay on. Open it again or quit from this icon.");
            var s = Store.Settings;
            s.Windows.ToldAboutTray = true;
            Store.SaveSettings(s);
        }
    }

    public void SetActive(string id)
    {
        Store.SetActive(id);
        Engine.SetPreset(Store.Active());
        PresetsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Quit()
    {
        _quitting = true;
        _window?.Close();
        _tray?.Dispose();
        Engine.Dispose();  // turns the trigger effects off
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    static readonly Lock LogLock = new();

    public static void Log(string? text, string file = "error.log")
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDir);
                var path = Path.Combine(LogDir, file);
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now:s}] {text}\n");
            }
        }
        catch (IOException) { }
    }

    bool _toldAboutError;

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception.ToString());
        e.Handled = true;
        if (_toldAboutError)  // a drawing bug would repeat every frame; say it once
            return;
        _toldAboutError = true;
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in {Path.Combine(LogDir, "error.log")}",
                        "Controller Studio Pro", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
