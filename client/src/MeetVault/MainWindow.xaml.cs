using MeetVault.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        RootFrame.Navigate(typeof(DashboardPage));
        Nav.SelectedItem = Nav.MenuItems[0];
        // Auto lifecycle: the overlay opens when a recording starts and closes when it
        // stops (paused keeps it open — the overlay dims itself through LiveChanged).
        LiveBridge.MeetingStateChanged += state =>
        {
            try
            {
                if (state == LiveBridge.MeetingState.Recording)
                {
                    // A dismissal mid-run sticks until the next meeting (review r17).
                    if (!OverlayWindow.UserDismissed) OverlayWindow.ShowOrActivate();
                }
                else if (state == LiveBridge.MeetingState.Idle)
                {
                    OverlayWindow.UserDismissed = false;
                    OverlayWindow.CloseIfOpen();
                }
            }
            catch (Exception ex) { MeetVault.Core.AppLog.Warn("overlay auto: " + ex.Message); }
        };
    }

    public void NavigateToLive() => NavigateTo(typeof(LivePage), "live");

    public void NavigateToDetails() => NavigateTo(typeof(DetailsPage), "details");

    public void NavigateToBackend() => NavigateTo(typeof(BackendPage), "backend");

    public void NavigateToAccount() => NavigateTo(typeof(LoginPage), "account");

    // Select the nav item (SelectionChanged navigates); if it is already selected,
    // navigate directly so the page still reloads with fresh Pending* state.
    private void NavigateTo(Type page, string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag);
        if (item is not null && !ReferenceEquals(Nav.SelectedItem, item)) { Nav.SelectedItem = item; return; }
        try { RootFrame.Navigate(page); }
        catch (Exception ex) { _ = ShowNavErrorAsync(tag, ex); }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Built-in Settings item has no Tag: IsSettingsSelected covers it (single Settings entry).
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        MeetVault.Core.AppLog.Info("nav: " + tag);
        try
        {
            RootFrame.Navigate(tag switch
            {
                "live" => typeof(LivePage),
                "meetings" => typeof(MeetingsPage),
                "details" => typeof(DetailsPage),
                "settings" => typeof(SettingsPage),
                "backend" => typeof(BackendPage),
                "account" => typeof(LoginPage),
                _ => typeof(DashboardPage),
            });
        }
        catch (Exception ex) { _ = ShowNavErrorAsync(tag, ex); }
    }
    // ponytail: dialog names the failing page + cause; Dashboard fallback keeps the app alive.
    private async Task ShowNavErrorAsync(string? tag, Exception ex)
    {
        // Users get a plain sentence; the diagnostic detail goes to the log, never the screen.
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "MeetVault");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "nav-errors.log"), DateTimeOffset.Now.ToString("o") + " page=" + (tag ?? "?") + " " + ex.GetType().Name + ": " + ex.Message + " STACK:" + (ex.StackTrace ?? string.Empty) + global::System.Environment.NewLine);
        }
        catch { }
        try
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Tf("Nav.ErrorTitle", tag ?? Loc.T("Nav.ErrorPageFallback")),
                Content = Loc.Tf("Nav.ErrorBody", Path.Combine(Path.GetTempPath(), "MeetVault")),
                CloseButtonText = Loc.T("Nav.BackToDashboard"),
                XamlRoot = RootFrame.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch { }
        MeetVault.Core.AppLog.Error("nav: '" + (tag ?? "?") + "' failed", ex);
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == "dashboard");
        RootFrame.Navigate(typeof(DashboardPage));
    }
}
