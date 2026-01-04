using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows;
using LocalWebServerInstance = AI_Test.LocalWebServer.LocalWebServer;

namespace AI_Test;

public partial class App : System.Windows.Application
{
    private readonly LocalWebServerInstance _localWebServer = new();
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _trayMenu;

    public static Uri? LocalServerBaseUri { get; private set; }
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var resourcesDirectory = Path.Combine(AppContext.BaseDirectory, "Resources");
        _localWebServer.StartAsync(resourcesDirectory).GetAwaiter().GetResult();
        LocalServerBaseUri = _localWebServer.BaseUri;

        InitializeTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeTrayIcon();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _localWebServer.StopAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
        }
        base.OnExit(e);
    }

    private void InitializeTrayIcon()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        _trayMenu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayMenu.Items.Add(showItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.Invoke(RequestShutdown);
        _trayMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "LLM测试助手",
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };

        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void DisposeTrayIcon()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_trayMenu is not null)
        {
            _trayMenu.Dispose();
            _trayMenu = null;
        }
    }

    private void ShowMainWindow()
    {
        var window = MainWindow;
        if (window is null)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();

        var wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = wasTopmost;
        window.Focus();
    }

    private async void RequestShutdown()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        try
        {
            DisposeTrayIcon();
        }
        catch
        {
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _localWebServer.StopAsync(cts.Token);
        }
        catch
        {
        }

        try
        {
            MainWindow?.Close();
        }
        catch
        {
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Environment.Exit(0);
        });

        Shutdown();
    }
}
