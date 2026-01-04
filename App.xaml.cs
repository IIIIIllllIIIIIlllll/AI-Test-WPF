using System.IO;
using System.Windows;
using LocalWebServerInstance = AI_Test.LocalWebServer.LocalWebServer;

namespace AI_Test;

public partial class App : Application
{
    private readonly LocalWebServerInstance _localWebServer = new();

    public static Uri? LocalServerBaseUri { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var resourcesDirectory = Path.Combine(AppContext.BaseDirectory, "Resources");
        _localWebServer.StartAsync(resourcesDirectory).GetAwaiter().GetResult();
        LocalServerBaseUri = _localWebServer.BaseUri;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _localWebServer.StopAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
