using System.Windows;

namespace ControllerStudioPro;

public partial class RenameDialog : Window
{
    public RenameDialog(string name, string description)
    {
        InitializeComponent();
        NameBox.Text = name;
        DescriptionBox.Text = description;
        Loaded += (_, _) => NameBox.SelectAll();
    }

    public string PresetName => NameBox.Text.Trim();
    public string Description => DescriptionBox.Text.Trim();

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (PresetName.Length == 0)
        {
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
