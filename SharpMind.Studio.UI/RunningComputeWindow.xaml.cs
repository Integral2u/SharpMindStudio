using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace SharpMind.Studio.UI;

public enum ExitComputeChoice
{
    /// <summary>Leave the instances running; this is the default.</summary>
    Keep,

    /// <summary>Destroy every running instance, then exit.</summary>
    Destroy,

    /// <summary>Stay in Studio.</summary>
    Cancel,
}

/// <summary>
/// Exit prompt shown when Vultr instances are still billing. Keeping them is the
/// default; destroying requires an explicit click.
/// </summary>
public partial class RunningComputeWindow : Window
{
    public RunningComputeWindow(RunningComputeSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        InitializeComponent();
        InstanceList.ItemsSource = summary.Instances;

        string count = summary.Count == 1 ? "1 instance" : $"{summary.Count} instances";
        string hourly = summary.HourlyCost.ToString("0.00", CultureInfo.InvariantCulture);
        SummaryText.Text =
            $"{count} are still running at ${hourly}/hour. Keeping them means the meter keeps "
            + "running after Studio closes.";
        KeepButton.Focus();
    }

    public ExitComputeChoice Choice { get; private set; } = ExitComputeChoice.Keep;

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        Choice = ExitComputeChoice.Keep;
        Close();
    }

    private void OnDestroy(object sender, RoutedEventArgs e)
    {
        Choice = ExitComputeChoice.Destroy;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = ExitComputeChoice.Cancel;
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Choice = ExitComputeChoice.Cancel;
        Close();
    }
}
