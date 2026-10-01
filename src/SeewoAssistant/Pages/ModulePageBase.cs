using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SeewoAssistant.Services;

namespace SeewoAssistant.Pages;

/// <summary>
/// Shared plumbing for every module page: receives the service graph and provides
/// the status-reporting helper.
/// </summary>
/// <remarks>
/// Pages are constructed by the <c>Frame</c> navigator, which passes the parameter
/// given to <c>Navigate</c>. Deriving from this base means each page declares its
/// own layout in XAML and gets the services without repeating the cast.
/// </remarks>
public abstract class ModulePageBase : Page
{
    /// <summary>The service graph. Never null after <see cref="OnNavigatedTo"/>.</summary>
    protected AppServices Services { get; private set; } = null!;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is AppServices services)
        {
            Services = services;
            OnServicesReady();
        }
    }

    /// <summary>
    /// Called once the services are available. Pages load their initial state here
    /// rather than in a constructor, because the constructor runs before navigation
    /// supplies the parameter.
    /// </summary>
    protected virtual void OnServicesReady()
    {
    }

    /// <summary>Reports a message to the shell's status bar.</summary>
    protected void Report(string message, StatusSeverity severity = StatusSeverity.Informational) =>
        Services.Report(message, severity);

    /// <summary>
    /// Runs an operation that talks to the OS, reporting success and failure through
    /// the status bar rather than throwing into the UI.
    /// </summary>
    protected async Task RunGuardedAsync(string description, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"{description} failed.", ex);
            Report($"{description}失败：{ex.Message}", StatusSeverity.Error);
        }
    }

    /// <summary>
    /// Serialises dialogs on this page.
    /// </summary>
    /// <remarks>
    /// WinUI allows only one <see cref="ContentDialog"/> open at a time, and calling
    /// <c>ShowAsync</c> while another is open throws
    /// <c>COMException: Only a single ContentDialog can be open at any time</c>. That
    /// exception used to escape the page and surface as an unhandled exception on the UI
    /// thread - reachable by clicking two confirmation buttons in quick succession, or by
    /// clicking one while a dialog was still open.
    ///
    /// The semaphore makes a second caller wait its turn instead of throwing, and the
    /// wait is bounded so a stuck dialog cannot block a page forever.
    /// </remarks>
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    /// <summary>Shows a confirmation dialog and returns whether the user accepted.</summary>
    /// <remarks>
    /// Returns false when another dialog is already showing rather than throwing: the
    /// caller asked a yes/no question, and "no" is the safe answer when the question
    /// could not be put to the user.
    /// </remarks>
    protected async Task<bool> ConfirmAsync(string title, string message, string acceptText = "继续", string cancelText = "取消")
    {
        if (!await _dialogGate.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true))
        {
            // Another dialog has been open for 30 seconds; do not stack a second one.
            return false;
        }

        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = acceptText,
                CloseButtonText = cancelText,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // A dialog can still fail for reasons outside our control (the page being
            // torn down mid-show, for instance). Reporting "not confirmed" is correct and
            // keeps the failure out of the unhandled-exception path.
            System.Diagnostics.Debug.WriteLine($"ConfirmAsync failed: {ex.Message}");
            return false;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    /// <summary>Shows an informational dialog.</summary>
    /// <remarks>
    /// Shares the gate with <see cref="ConfirmAsync"/> so the two can never be open at
    /// the same time.
    /// </remarks>
    protected async Task ShowDialogAsync(string title, string message)
    {
        if (!await _dialogGate.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot,
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShowDialogAsync failed: {ex.Message}");
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    /// <summary>Sets the text of a status badge and colours it by state.</summary>
    protected static void SetBadge(Border badge, TextBlock text, string label, bool active, bool warning = false)
    {
        text.Text = label;

        var backgroundKey = active
            ? (warning ? "SystemFillColorCautionBackgroundBrush" : "SystemFillColorSuccessBackgroundBrush")
            : "SystemFillColorNeutralBackgroundBrush";

        var foregroundKey = active
            ? (warning ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush")
            : "TextFillColorSecondaryBrush";

        if (Application.Current.Resources.TryGetValue(backgroundKey, out var background) &&
            background is Microsoft.UI.Xaml.Media.Brush backgroundBrush)
        {
            badge.Background = backgroundBrush;
        }

        if (Application.Current.Resources.TryGetValue(foregroundKey, out var foreground) &&
            foreground is Microsoft.UI.Xaml.Media.Brush foregroundBrush)
        {
            text.Foreground = foregroundBrush;
        }
    }
}
