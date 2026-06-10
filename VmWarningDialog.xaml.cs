using System.Windows;

namespace Lyrictified;

public partial class VmWarningDialog : Window
{
    public VmWarningDialog()
    {
        InitializeComponent();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
