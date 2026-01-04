using System.Windows;

namespace AI_Test;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await Browser.EnsureCoreWebView2Async();

        var baseUri = App.LocalServerBaseUri ?? new Uri("about:blank");
        Browser.Source = baseUri;
    }
}
