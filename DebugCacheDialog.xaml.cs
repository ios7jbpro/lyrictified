using System.Windows;

namespace Lyrictified;

public partial class DebugCacheDialog : Window
{
    public bool IgnoreCache { get; private set; }

    public DebugCacheDialog()
    {
        InitializeComponent();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        IgnoreCache = false;
        Close();
    }

    private void BtnYes_OnClick(object sender, RoutedEventArgs e)
    {
        IgnoreCache = true;
        Close();
    }

    private void BtnNo_OnClick(object sender, RoutedEventArgs e)
    {
        IgnoreCache = false;
        Close();
    }
}
