using System.Windows;

namespace SharpMind.Studio.UI;

public partial class TrayHintWindow : Window
{
    public TrayHintWindow()
    {
        InitializeComponent();
    }

    public bool IgnoreRequested { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnIgnore(object sender, RoutedEventArgs e)
    {
        IgnoreRequested = true;
        Close();
    }
}
