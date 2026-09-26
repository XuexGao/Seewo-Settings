using Microsoft.UI.Xaml;
using SeewoAssistant.Services;

namespace SeewoAssistant;

/// <summary>
/// Application entry point and process-wide service host.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    /// <summary>The single instance of this application.</summary>
    public static new App Current => (App)Application.Current;

    /// <summary>The composition root, available to every page.</summary>
    public AppServices Services { get; private set; } = null!;

    /// <summary>The main window, or null before it is created.</summary>
    public MainWindow? MainWindow => _window;

    public App()
    {
        InitializeComponent();

        // An unhandled exception on the UI thread would otherwise terminate the
        // process with no trace. Logging it and keeping the app alive is the right
        // trade for a tray app: a failure in one panel should not take down the
        // privacy monitor that is running in the background.
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = new AppServices();

        Services.Logger.Info("SeewoAssistant starting.");

        // Notification registration must happen before the first alert is raised.
        Services.PrivacyNotifier.Initialize();

        _window = new MainWindow(Services);
        _window.Activate();

        Services.StartBackgroundServices();

        Services.Logger.Info("SeewoAssistant started.");
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Services?.Logger.Error("Unhandled exception on the UI thread.", e.Exception);
        }
        catch
        {
            // Logging itself failed; nothing further can be done here.
        }

        // Do not mark the exception handled: doing so would leave the UI in an
        // undefined state. The process exits, and the shutdown path in
        // AppServices.DisposeAsync is still driven by MainWindow.Closed.
    }

    /// <summary>
    /// Brings the main window to the foreground, restoring it if it was minimised.
    /// Used by the tray icon and by notification clicks.
    /// </summary>
    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Activate();
        _window.RestoreFromTray();
    }
}
