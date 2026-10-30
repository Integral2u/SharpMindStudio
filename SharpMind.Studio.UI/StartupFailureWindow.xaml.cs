using System.Windows;
using Media = System.Windows.Media;

namespace SharpMind.Studio.UI;

public enum StartupFailureChoice
{
    Retry,
    Restart,
    ResetLocal,
    Exit,
}

public partial class StartupFailureWindow : Window
{
    public StartupFailureWindow(string message, Media.Brush accent)
    {
        InitializeComponent();
        MessageText.Text = message;
        TitleText.Foreground = accent;
        RetryButton.Background = accent;
        RetryButton.BorderBrush = accent;
    }

    public StartupFailureChoice Choice { get; private set; } = StartupFailureChoice.Exit;

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        Choice = StartupFailureChoice.Retry;
        Close();
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        Choice = StartupFailureChoice.Restart;
        Close();
    }

    private void OnResetLocal(object sender, RoutedEventArgs e)
    {
        Choice = StartupFailureChoice.ResetLocal;
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Choice = StartupFailureChoice.Exit;
        Close();
    }
}


