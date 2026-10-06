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
    }

    public void NavigateToLive()
    {
        try
        {
            RootFrame.Navigate(typeof(LivePage));
            foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
                if ((item.Tag as string) == "live") Nav.SelectedItem = item;
        }
        catch (Exception ex) { _ = ShowNavErrorAsync("live", ex); }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Built-in Settings item has no Tag: IsSettingsSelected covers it (single Settings entry).
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        try
        {
            RootFrame.Navigate(tag switch
            {
                "live" => typeof(LivePage),
                "meetings" => typeof(MeetingsPage),
                "details" => typeof(DetailsPage),
                "settings" => typeof(SettingsPage),
                _ => typeof(DashboardPage),
            });
        }
        catch (Exception ex) { _ = ShowNavErrorAsync(tag, ex); }
    }
    // ponytail: dialog names the failing page + cause; Dashboard fallback keeps the app alive.
    private async Task ShowNavErrorAsync(string? tag, Exception ex)
    {
        var detail = ex.GetType().Name + ": " + ex.Message;
        if (ex.InnerException is not null) detail += " | Inner: " + ex.InnerException.Message;
        var stack = (ex.StackTrace ?? string.Empty).Replace((char)13, ' ').Replace((char)10, ' ');
        if (stack.Length > 900) stack = stack.Substring(0, 900);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "MeetVault");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "nav-errors.log"), DateTimeOffset.Now.ToString("o") + " page=" + (tag ?? "?") + " " + ex.GetType().Name + ": " + ex.Message + " STACK:" + (ex.StackTrace ?? string.Empty) + global::System.Environment.NewLine);
        }
        catch { }
        try
        {
            var dialog = new ContentDialog { Title = "Could not open " + (tag ?? "page"), Content = detail + " [" + stack + "]", CloseButtonText = "Back to Dashboard", XamlRoot = RootFrame.XamlRoot };
            await dialog.ShowAsync();
        }
        catch { }
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == "dashboard");
        RootFrame.Navigate(typeof(DashboardPage));
    }
}
