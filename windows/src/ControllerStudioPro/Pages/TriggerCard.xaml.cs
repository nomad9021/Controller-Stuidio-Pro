using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ControllerStudio;

namespace ControllerStudioPro.Pages;

/// <summary>Editor for one trigger (its DataContext is the trigger's TriggerConfig).</summary>
public partial class TriggerCard : UserControl
{
    public static readonly DependencyProperty VirtualOnProperty =
        DependencyProperty.Register(nameof(VirtualOn), typeof(bool), typeof(TriggerCard));

    MainWindow? _owner;
    string _side = "l2";

    public TriggerCard()
    {
        InitializeComponent();
    }

    TriggerConfig? Config => DataContext as TriggerConfig;

    /// <summary>The virtual controller is on (hides the notes that say it's needed).</summary>
    public bool VirtualOn { get => (bool)GetValue(VirtualOnProperty); set => SetValue(VirtualOnProperty, value); }

    public void Attach(MainWindow owner, string side)
    {
        _owner = owner;
        _side = side;
        SideName.Text = side.ToUpperInvariant();
    }

    public void Update(EngineSnapshot snap)
    {
        if (Config is not { } t)
            return;
        double pull = (snap.Input?.Trigger(_side) ?? 0) / 255.0;
        if (Math.Abs(pull - t.Pull) > 0.001)
            t.Pull = pull;
        PullText.Text = $"{Math.Round(pull * 100)}%";
        var firing = _side == "l2" ? snap.FiringL2 : snap.FiringR2;
        foreach (var r in t.Reactions)
            r.Firing = r.Id == firing;
        bool virt = _owner?.Settings.Output.Virtual ?? false;
        if (VirtualOn != virt)
            VirtualOn = virt;
    }

    // ---- feel

    void Shape_Click(object sender, RoutedEventArgs e)
    {
        if (Config is { } t && sender is FrameworkElement { Tag: string shape })
            t.Feel.Zones = [.. shape.Split(',').Select(int.Parse)];
    }

    void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (Config is { } t && sender is FrameworkElement { Tag: string d })
        {
            int n = int.Parse(d);
            t.Feel.Zones = [.. t.Feel.Zones.Select(v => v > 0 ? Math.Clamp(v + n, 1, 8) : v)];
        }
    }

    void FeelIt_Click(object sender, RoutedEventArgs e)
    {
        if (Config is { } t)
            App.Engine.TestFeel(_side, t.Feel, 4);
    }

    // ---- reactions

    static Reaction? ReactionOf(object sender) => (sender as FrameworkElement)?.DataContext as Reaction;

    void Chips_Loaded(object sender, RoutedEventArgs e)
    {
        var panel = (WrapPanel)sender;
        if (panel.Children.Count > 0 || ReactionOf(sender) is not { } r)
            return;
        foreach (var b in Names.ReactionButtons)
        {
            var chip = new ToggleButton
            {
                Content = Names.Button(b),
                IsChecked = r.Buttons.Contains(b),
                Style = (Style)FindResource("Chip"),
            };
            var id = b;
            chip.Click += (_, _) =>
            {
                r.Buttons = chip.IsChecked == true ? [.. r.Buttons.Append(id).Distinct()] : [.. r.Buttons.Where(x => x != id)];
            };
            panel.Children.Add(chip);
        }
    }

    void Move(object sender, int by)
    {
        if (Config is not { } t || ReactionOf(sender) is not { } r)
            return;
        int i = t.Reactions.IndexOf(r), to = i + by;
        if (i >= 0 && to >= 0 && to < t.Reactions.Count)
            t.Reactions.Move(i, to);
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, 1);

    void DeleteReaction_Click(object sender, RoutedEventArgs e)
    {
        if (Config is { } t && ReactionOf(sender) is { } r)
            t.Reactions.Remove(r);
    }

    void FeelReaction_Click(object sender, RoutedEventArgs e)
    {
        if (ReactionOf(sender) is { } r)
            App.Engine.TestReaction(_side, r.Effect, hold: r.When is not ("buttons" or "gear" or "slam"), 1.2);
    }

    void AddReaction_Click(object sender, RoutedEventArgs e)
    {
        if (Config is not { } t)
            return;
        var r = new Reaction
        {
            Id = "r" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x"),
            When = "buttons",
            Effect = new ReactionEffect { Type = "kick", Strength = 7, DurationMs = 60, Start = 0, Freq = 45 },
        };
        t.Reactions.Add(r);
        // Open the new one, like the Linux version does.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ReactionList.ItemContainerGenerator.ContainerFromItem(r) is DependencyObject c && Find<Expander>(c) is { } ex)
                ex.IsExpanded = true;
        });
    }

    static T? Find<T>(DependencyObject d) where T : DependencyObject
    {
        if (d is T t)
            return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (Find<T>(VisualTreeHelper.GetChild(d, i)) is { } found)
                return found;
        return null;
    }

    // ---- throw

    void ResetThrow_Click(object sender, RoutedEventArgs e)
    {
        if (Config is { } t)
            t.Output = new TriggerOutput();
    }

    void GotoOutput_Click(object sender, RoutedEventArgs e) => _owner?.Select("output");
}
