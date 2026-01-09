using System.ComponentModel;
using System.Windows;

namespace AI_Test;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await Browser.EnsureCoreWebView2Async();

        var baseUri = App.LocalServerBaseUri ?? new Uri("about:blank");
        Browser.Source = baseUri;
    }

    private async void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(new Uri("/", UriKind.Relative));
    }

    private async void ConcurrencyButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(new Uri("concurrency.html", UriKind.Relative));
    }

    private async Task NavigateAsync(Uri relativeOrAbsoluteUri)
    {
        await Browser.EnsureCoreWebView2Async();

        if (relativeOrAbsoluteUri.IsAbsoluteUri)
        {
            Browser.Source = relativeOrAbsoluteUri;
            return;
        }

        var baseUri = App.LocalServerBaseUri ?? new Uri("about:blank");
        Browser.Source = new Uri(baseUri, relativeOrAbsoluteUri);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (App.IsExiting)
        {
            try
            {
                Browser.Dispose();
            }
            catch
            {
            }

            return;
        }

        e.Cancel = true;
        Hide();
    }
}
