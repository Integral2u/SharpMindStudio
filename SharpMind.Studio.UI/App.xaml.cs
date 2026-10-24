using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace SharpMind.Studio.UI;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\SharpMind.Studio.UI.SingleInstance";
    private const string ActivationPipeName = "SharpMind.Studio.UI.Activate";
    private Mutex? _singleInstance;
    private CancellationTokenSource? _activationCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(
            initiallyOwned: true,
            name: MutexName,
            createdNew: out bool createdNew);

        if (!createdNew)
        {
            if (!TryActivateExisting())
            {
                WinForms.MessageBox.Show(
                    "SharpMind Studio is already running. Double-click its tray icon to reopen it.",
                    "SharpMind Studio",
                    WinForms.MessageBoxButtons.OK,
                    WinForms.MessageBoxIcon.Information);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _activationCts = new CancellationTokenSource();
        _ = ListenForActivationAsync(_activationCts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCts?.Cancel();
        _activationCts?.Dispose();
        _activationCts = null;

        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch
        {
            // The process may have lost ownership during shutdown.
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static bool TryActivateExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", ActivationPipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(1000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("show");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                await reader.ReadLineAsync(cancellationToken);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (MainWindow is MainWindow window)
                        window.ShowFromTray();
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A malformed client should not stop the listener.
            }
        }
    }
}


