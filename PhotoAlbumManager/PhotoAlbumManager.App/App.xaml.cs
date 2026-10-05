using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotoAlbumManager;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var dialogReady = _window?.Content?.XamlRoot is not null;
        if (!dialogReady)
            return;

        e.Handled = true;
        var dialog = new ContentDialog
        {
            Title = "Photo Album Manager",
            Content = e.Exception.Message,
            CloseButtonText = "OK",
            XamlRoot = _window!.Content.XamlRoot
        };
        _ = dialog.ShowAsync();
    }
}
