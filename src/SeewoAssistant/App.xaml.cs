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

    /// <summary>
    /// The UI thread's dispatcher.
    /// </summary>
    /// <remarks>
    /// WinUI's <c>Application</c> has no <c>DispatcherQueue</c> property, so it is
    /// captured from the main window once it exists. Background threads (the
    /// privacy monitor's registry watchers, the notification callback) use this to
    /// marshal onto the UI thread. It is null only before the window is created.
    /// </remarks>
    public Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher { get; private set; }

    /// <summary>The main window, or null before it is created.</summary>
    public MainWindow? MainWindow => _window;

    public App()
    {
        InitializeComponent();

        // An unhandled exception on the UI thread would otherwise terminate the
        // process with no trace.
        UnhandledException += OnUnhandledException;

        // Exceptions on background threads do not reach UnhandledException. Without
        // these two handlers a fault on the frame pump or the registry watcher kills
        // the process with nothing written anywhere, which is indistinguishable from
        // a silent crash.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashReporter.WriteCrashReport(
                args.ExceptionObject as Exception, "后台线程未处理异常");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashReporter.WriteCrashReport(args.Exception, "未观察的任务异常");

            // Marking it observed prevents the process from being torn down for a
            // fault that has already been recorded.
            args.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = new AppServices();

        Services.Logger.Info("SeewoAssistant starting.");

        // Notification registration must happen before the first alert is raised.
        Services.PrivacyNotifier.Initialize();

        // Capture the UI dispatcher before anything can raise an event that needs
        // to marshal onto the UI thread.
        UiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

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

        // Write a standalone crash file as well as logging. When the app dies, the
        // log is the only record, and the user needs something they can find and send
        // without knowing where the log lives. This is also what makes a crash report
        // actionable rather than "it closed".
        try
        {
            CrashReporter.WriteCrashReport(e.Exception, "UI 线程未处理异常");
        }
        catch
        {
            // Reporting the crash must never itself crash.
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
