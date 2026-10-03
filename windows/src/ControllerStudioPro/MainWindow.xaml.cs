using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ControllerStudio;
using ControllerStudioPro.Pages;

namespace ControllerStudioPro;

/// <summary>A preset in the sidebar.</summary>
public partial class PresetItem : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _lightbar = "#ffffff";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _builtin;
}

/// <summary>A page that shows live controller data.</summary>
public interface ILivePage
{
    void Update(EngineSnapshot snap);
}

public partial class MainWindow : Window
{
    static readonly Dictionary<string, (string Title, string Subtitle)> Pages = new()
    {
        ["lighting"] = ("Lighting", "Your light bar and player lights, kept the way you set them."),
        ["output"] = ("Output", "What games receive from your controller."),
        ["tester"] = ("Controller Tester", "Everything the controller reports, live. Preset effects pause while the Trigger Lab runs."),
    };

    readonly ObservableCollection<PresetItem> _items = [];
    readonly DispatcherTimer _live, _saveDraft, _saveSettings;
    readonly PresetPage _presetPage;
    LightingPage? _lightingPage;
    OutputPage? _outputPage;
    TesterPage? _testerPage;
    Preset? _draft;
    string _view = "";
    bool _syncing;
    bool _virtualBefore;

    public AppSettings Settings { get; }
    public EngineSnapshot? Snapshot { get; private set; }
    public List<string> Addresses { get; } = Native.LocalAddresses();

    public MainWindow()
    {
        InitializeComponent();
        PresetList.ItemsSource = _items;
        Settings = App.Store.Settings;
        _virtualBefore = Settings.Output.Virtual;

        _saveDraft = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _saveDraft.Tick += (_, _) => SaveDraft();
        _saveSettings = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _saveSettings.Tick += (_, _) => SaveSettings();
        _live = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _live.Tick += (_, _) => Tick();
        Settings.Changed += (_, _) => Restart(_saveSettings);
        _presetPage = new PresetPage(this);

        App.Current.PresetsChanged += OnPresetsChanged;
        Reload();
        Select("preset:" + App.Store.ActiveId);
        Tick();
        _live.Start();
        StateChanged += (_, _) => _live.IsEnabled = WindowState != WindowState.Minimized;
    }

    static void Restart(DispatcherTimer t)
    {
        t.Stop();
        t.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveDraft();
        SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _live.Stop();
        App.Current.PresetsChanged -= OnPresetsChanged;
        App.Engine.StopTest();
        base.OnClosed(e);
        App.Current.WindowClosed();
    }

    void OnPresetsChanged(object? sender, EventArgs e)
    {
        Reload();
        if (_draft != null)
            UpdateHeader();
    }

    // ------------------------------------------------------------------ sidebar

    void Reload()
    {
        _syncing = true;
        var all = App.Store.All();
        var active = App.Store.ActiveId;
        _items.Clear();
        foreach (var p in all)
            _items.Add(new PresetItem { Id = p.Id, Name = p.Name, Lightbar = p.Lightbar, IsActive = p.Id == active, Builtin = p.Builtin });
        if (_draft != null)
            PresetList.SelectedItem = _items.FirstOrDefault(i => i.Id == _draft.Id);
        int hidden = App.Store.HiddenCount;
        RestoreButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreText.Text = $"Restore built-in presets ({hidden})";
        _syncing = false;
    }

    void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && PresetList.SelectedItem is PresetItem item && _view != "preset:" + item.Id)
            Select("preset:" + item.Id);
    }

    void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && PageList.SelectedItem is ListViewItem { Tag: string page } && _view != page)
            Select(page);
    }

    public void Select(string view)
    {
        SaveDraft();
        App.Engine.StopTest();
        if (_draft != null)
            _draft.Changed -= DraftChanged;
        _draft = null;
        _syncing = true;
        if (view.StartsWith("preset:"))
        {
            var id = view["preset:".Length..];
            _draft = App.Store.Get(id) ?? App.Store.Active();
            _view = "preset:" + _draft.Id;
            _draft.Changed += DraftChanged;
            PresetList.SelectedItem = _items.FirstOrDefault(i => i.Id == _draft.Id);
            PageList.SelectedItem = null;
            PresetActions.Visibility = Visibility.Visible;
            _presetPage.Show(_draft);
            PageHost.Content = _presetPage;
            UpdateHeader();
        }
        else
        {
            _view = view;
            PresetList.SelectedItem = null;
            PageList.SelectedItem = PageList.Items.OfType<ListViewItem>().FirstOrDefault(i => (string)i.Tag == view);
            PresetActions.Visibility = Visibility.Collapsed;
            (PageTitle.Text, PageSubtitle.Text) = Pages[view];
            PageHost.Content = view switch
            {
                "lighting" => _lightingPage ??= new LightingPage(this),
                "output" => _outputPage ??= new OutputPage(this),
                _ => _testerPage ??= new TesterPage(this),
            };
        }
        _syncing = false;
        Scroller.ScrollToTop();
        if (Snapshot != null)
            (PageHost.Content as ILivePage)?.Update(Snapshot);
    }

    void UpdateHeader()
    {
        if (_draft is null)
            return;
        PageTitle.Text = _draft.Name;
        PageSubtitle.Text = _draft.Summary;
        bool active = _draft.Id == App.Store.ActiveId;
        InUse.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UseButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        ResetItem.IsEnabled = _draft.Builtin && _draft.Modified;
        DeleteItem.IsEnabled = _items.Count > 1;
    }

    // ------------------------------------------------------------------ saving

    void DraftChanged(object? sender, EventArgs e)
    {
        Restart(_saveDraft);
        if (_draft is null)
            return;
        var item = _items.FirstOrDefault(i => i.Id == _draft.Id);
        if (item != null)
        {
            item.Name = _draft.Name;
            item.Lightbar = _draft.Lightbar;
        }
        PageTitle.Text = _draft.Name;
        PageSubtitle.Text = _draft.Summary;
    }

    void SaveDraft()
    {
        if (!_saveDraft.IsEnabled)
            return;
        _saveDraft.Stop();
        if (_draft is null)
            return;
        try
        {
            App.Store.Update(_draft);
        }
        catch (KeyNotFoundException)
        {
            return;  // deleted meanwhile
        }
        if (_draft.Id == App.Store.ActiveId)
            App.Engine.SetPreset(_draft);
        if (_draft.Builtin)
            _draft.Modified = true;
        UpdateHeader();
    }

    void SaveSettings()
    {
        if (!_saveSettings.IsEnabled)
            return;
        _saveSettings.Stop();
        App.Store.SaveSettings(Settings);
        App.Engine.SetSettings(Settings);
        if (Settings.Output.Virtual != _virtualBefore)
        {
            _virtualBefore = Settings.Output.Virtual;
            try { Native.SetMoonlightHidden(_virtualBefore); } catch (Exception e) when (e is UnauthorizedAccessException or System.IO.IOException) { }
        }
    }

    /// <summary>Save settings right away (for changes that should take effect now).</summary>
    public void SaveSettingsNow()
    {
        Restart(_saveSettings);
        SaveSettings();
    }

    // ------------------------------------------------------------------ live

    void Tick()
    {
        var s = App.Engine.Snapshot();
        Snapshot = s;
        var d = s.Device;
        DeviceName.Text = d?.Model ?? "No controller";
        DeviceMeta.Text = d is null ? "Press the PS button to connect" : d.Bluetooth ? "Bluetooth" : "USB";
        DeviceIcon.SetResourceReference(ForegroundProperty, d is null ? "TextFillColorTertiaryBrush" : "AccentTextFillColorPrimaryBrush");
        if (s.Input is { } st)
        {
            Battery.Visibility = Visibility.Visible;
            bool charging = st.Charging == "charging";
            BatteryFill.Width = 20 * st.Battery / 100.0;
            BatteryText.Text = $"{st.Battery}%";
            BatteryFill.SetResourceReference(Border.BackgroundProperty,
                charging ? "SystemFillColorSuccessBrush" : st.Battery <= 15 ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            Battery.ToolTip = $"Battery {st.Battery}%{(charging ? ", charging" : "")}";
        }
        else
            Battery.Visibility = Visibility.Collapsed;
        EffectsSwitch.IsChecked = s.Enabled;
        Banner.Visibility = d is null ? Visibility.Visible : Visibility.Collapsed;
        BannerText.Text = "Controller not connected. Press the PS button to wake it, or plug it in with USB.";
        (PageHost.Content as ILivePage)?.Update(s);
    }

    void EffectsSwitch_Click(object sender, RoutedEventArgs e) => App.Engine.Enabled = EffectsSwitch.IsChecked == true;

    // ------------------------------------------------------------------ preset actions

    void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
            return;
        SaveDraft();
        App.Current.SetActive(_draft.Id);
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreMenu.IsOpen = true;
    }

    void Duplicate(string id)
    {
        SaveDraft();
        var copy = App.Store.Duplicate(id);
        Reload();
        Select("preset:" + copy.Id);
    }

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_draft != null)
            Duplicate(_draft.Id);
    }

    void NewPreset_Click(object sender, RoutedEventArgs e) => Duplicate(_draft?.Id ?? App.Store.ActiveId);

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
            return;
        _saveDraft.Stop();
        App.Store.Reset(_draft.Id);
        if (_draft.Id == App.Store.ActiveId)
            App.Engine.SetPreset(App.Store.Active());
        Reload();
        Select(_view);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_draft != null)
            AskDelete(_draft.Id);
    }

    void AskDelete(string id)
    {
        var p = _items.FirstOrDefault(i => i.Id == id);
        if (p is null)
            return;
        var body = p.Builtin
            ? "It's a built-in preset, so you can bring it back later with Restore built-in presets."
            : "This can't be undone.";
        if (MessageBox.Show(this, body, $"Delete “{p.Name}”?", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                            MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        if (_draft?.Id == id)
        {
            _saveDraft.Stop();
            _draft.Changed -= DraftChanged;
        }
        try
        {
            App.Store.Delete(id);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Controller Studio Pro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        App.Engine.SetPreset(App.Store.Active());
        Reload();
        if (_view == "preset:" + id)
        {
            _draft = null;
            Select("preset:" + App.Store.ActiveId);
        }
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
            return;
        var dlg = new RenameDialog(_draft.Name, _draft.Summary) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;
        _draft.Name = dlg.PresetName;
        _draft.Summary = dlg.Description;
        Restart(_saveDraft);
        SaveDraft();
        Reload();
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        App.Store.RestoreBuiltins();
        Reload();
    }

    static PresetItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as PresetItem;

    void Ctx_Use(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            SaveDraft();
            App.Current.SetActive(item.Id);
        }
    }

    void Ctx_Duplicate(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            Duplicate(item.Id);
    }

    void Ctx_Delete(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            AskDelete(item.Id);
    }

    void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is { } item)
            AskDelete(item.Id);
    }
}
