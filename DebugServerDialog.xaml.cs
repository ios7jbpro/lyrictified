using System.Windows;

namespace Lyrictified;

public partial class DebugServerDialog : Window
{
    public string? ServerUrl { get; private set; }

    public DebugServerDialog()
    {
        InitializeComponent();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        var input = ServerUrlTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(input))
        {
            if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                input = "http://" + input;
            }
            if (!input.EndsWith('/'))
            {
                input += "/";
            }
            ServerUrl = input;
            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
