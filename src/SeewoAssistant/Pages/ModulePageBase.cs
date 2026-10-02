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
            TryShowIntro();
        }
    }

    // ------------------------------------------------------------------ intros

    /// <summary>
    /// Stable key for this page's one-time intro, or null for a page that has none.
    /// </summary>
    /// <remarks>
    /// A key rather than the page type name: renaming a class would otherwise re-show
    /// every intro, and the stored list has to survive refactoring.
    /// </remarks>
    protected virtual string? IntroKey => null;

    /// <summary>Heading of the one-time intro, or null to show none.</summary>
    protected virtual string? IntroTitle => null;

    /// <summary>Body of the one-time intro.</summary>
    protected virtual string? IntroBody => null;

    /// <summary>
    /// Shows this page's explanation the first time the page is opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The explanations used to sit permanently at the top of each page. That cost every
    /// visit a paragraph of scrolling to reach the controls, and the same text was read
    /// at most once. Moving it into a one-time dialog gives the page back to the things
    /// the user actually came for, while still explaining the limitation before it is hit.
    /// </para>
    /// <para>
    /// The "already seen" flag is recorded <em>before</em> the dialog is shown. If showing
    /// it fails, the user loses the explanation once rather than being prompted on every
    /// single visit, which is the worse of the two failures.
    /// </para>
    /// <para>
    /// It also waits for <see cref="FrameworkElement.Loaded"/> rather than running
    /// directly from <c>OnNavigatedTo</c>. A <see cref="ContentDialog"/> needs a
    /// <c>XamlRoot</c>, and during navigation the page is not in the tree yet, so
    /// <c>ShowAsync</c> throws and — with the flag already recorded — the explanation
    /// would be silently lost. Loaded is the first point where it is reliably available.
    /// </para>
    /// </remarks>
    private void TryShowIntro()
    {
        if (IntroKey is null || IntroTitle is null || IntroBody is null)
        {
            return;
        }

        if (!Services.Settings.ShowPageIntros || Services.Settings.SeenPageIntros.Contains(IntroKey))
        {
            return;
        }

        // One-shot: without this the handler would fire again on every re-entry to the
        // visual tree and try to show a second dialog.
        void OnLoaded(object sender, RoutedEventArgs args)
        {
            Loaded -= OnLoaded;
            _ = ShowIntroOnceAsync();
        }

        Loaded += OnLoaded;
    }

    /// <summary>Records the intro as seen and shows it.</summary>
    private async Task ShowIntroOnceAsync()
    {
        // Captured into locals so the compiler knows these cannot be null inside the
        // dialog's callback, where a property read would not be narrowed.
        var key = IntroKey;
        var title = IntroTitle;
        var body = IntroBody;

        if (key is null || title is null || body is null)
        {
            return;
        }

        // Re-check: the page may have been opened twice in quick succession, or the
        // explanation may have been reset from the settings page in between.
        if (Services.Settings.SeenPageIntros.Contains(key))
        {
            return;
        }

        Services.Settings.SeenPageIntros.Add(key);
        Services.SaveSettings();

        await ShowIntroAsync(key, title, body);
    }

    /// <summary>Shows a one-time explanation.</summary>
    /// <remarks>
    /// The close button says「知道了」rather than the generic「关闭」or「取消」. The automated
    /// smoke test dismisses these dialogs by button name, and several pages have real
    /// buttons labelled「取消」/「确定」that the sweep is explicitly told not to click
    /// because they rewrite configuration. A dismissal helper matching those names would
    /// press exactly the controls the test avoids, so this dialog carries a label no page
    /// button uses.
    /// </remarks>
    private async Task ShowIntroAsync(string key, string title, string message)
    {
        if (!await _dialogGate.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                Title = title,
                Content = body,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            // A way back to the explanation after dismissing it, so "once" does not mean
            // "gone for good" for someone who clicked past it too quickly.
            var again = new HyperlinkButton { Content = "以后可以在「设置」里重新显示这些说明" };
            again.Click += (_, _) =>
            {
                Services.Settings.SeenPageIntros.Remove(key);
                Services.SaveSettings();
                dialog.Hide();
                Report("已重置当前页面的说明。下次进入时会重新显示。");
            };

            body.Children.Add(again);

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Navigation during startup can race the XamlRoot becoming available. The
            // key is already recorded, so a failure here costs the user the explanation
            // once rather than re-prompting on every visit.
            System.Diagnostics.Debug.WriteLine($"ShowIntroAsync failed: {ex.Message}");
        }
        finally
        {
            _dialogGate.Release();
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
