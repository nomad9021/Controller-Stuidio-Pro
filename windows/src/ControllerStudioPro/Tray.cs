using System.Windows;
using Forms = System.Windows.Forms;

namespace ControllerStudioPro;

/// <summary>The notification area icon: open the window, switch presets, toggle effects, quit.</summary>
sealed class Tray : IDisposable
{
    readonly App _app;
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ContextMenuStrip _menu = new();

    public Tray(App app)
    {
        _app = app;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "Controller Studio Pro",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _app.ShowWindow();
        };
        _menu.Opening += (_, _) => Build();
        Build();
    }

    void Build()
    {
        _menu.Items.Clear();
        var open = new Forms.ToolStripMenuItem("Open Controller Studio Pro", null, (_, _) => _app.ShowWindow());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var presets = new Forms.ToolStripMenuItem("Preset");
        var active = App.Store.ActiveId;
        foreach (var p in App.Store.All())
        {
            var id = p.Id;
            presets.DropDownItems.Add(new Forms.ToolStripMenuItem(p.Name, null, (_, _) => _app.SetActive(id))
            {
                Checked = id == active,
            });
        }
        _menu.Items.Add(presets);
        _menu.Items.Add(new Forms.ToolStripMenuItem("Trigger effects", null,
            (_, _) => App.Engine.Enabled = !App.Engine.Enabled) { Checked = App.Engine.Enabled });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("Quit", null, (_, _) => _app.Quit()));
    }

    public void Tell(string title, string text) => _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
