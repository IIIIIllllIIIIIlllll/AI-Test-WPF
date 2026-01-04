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
