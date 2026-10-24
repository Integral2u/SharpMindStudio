using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SharpMind.Studio.Core.Storage;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SharpMind.Studio.UI;

public partial class MainWindow : Window
{
    private readonly StudioProcess _studio = new();
    private WinForms.NotifyIcon? _tray;
    private bool _exiting;
    private bool _balloonShown;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        StudioPaths.EnsureCreated();
        Browser.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(StudioPaths.Projects, "WebView2"),
        };
        Browser.NavigationCompleted += OnNavigationCompleted;
        SetupTray();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Starting the local engine…";
            await _studio.StartAsync();

            StatusText.Text = "Opening the instrument panel…";
            await Browser.EnsureCoreWebView2Async();
            Browser.Source = StudioProcess.BaseUri;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Studio could not start: {ex.Message}";
            WinForms.MessageBox.Show(
                ex.Message,
                "SharpMind Studio",
                WinForms.MessageBoxButtons.OK,
                WinForms.MessageBoxIcon.Error);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            StartupOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        StatusText.Text = $"The Studio UI did not load (HTTP {e.HttpStatusCode}).";
    }


    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "SharpMind Studio",
            Icon = LoadTrayIcon(),
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Studio", null, (_, _) => ShowFromTray());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => BeginExit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        string? executable = Environment.ProcessPath;
        if (executable is not null)
        {
            Drawing.Icon? icon = Drawing.Icon.ExtractAssociatedIcon(executable);
            if (icon is not null) return icon;
        }
        return Drawing.SystemIcons.Application;
    }

    public void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowFromTray);
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Browser.InvalidateVisual();
        Browser.UpdateLayout();
        Browser.Focus();
        Browser.CoreWebView2?.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'))");
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        Hide();
        ShowTrayHint();
    }

    private void ShowTrayHint()
    {
        if (_balloonShown || _tray is null) return;
        _balloonShown = true;
        _tray.ShowBalloonTip(
            2500,
            "SharpMind Studio",
            "Closing the window keeps Studio running here. Double-click to reopen.",
            WinForms.ToolTipIcon.Info);
    }

    private void BeginExit()
    {
        if (_exiting) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(BeginExit));
            return;
        }

        _exiting = true;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        try
        {
            Close();
        }
        catch
        {
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _studio.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                try
                {
                    _ = Dispatcher.BeginInvoke(new Action(ShutdownNow));
                }
                catch
                {
                    Environment.Exit(0);
                }
            }
        });

        _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => Environment.Exit(0));
    }

    private void ShutdownNow()
    {
        try
        {
            Close();
        }
        catch
        {
        }

        System.Windows.Application.Current.Shutdown();
    }

    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnTitleBarClose(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    private void FitToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;
        Width = Math.Min(1320, work.Width * 0.92);
        Height = Math.Min(860, work.Height * 0.86);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }
}
































