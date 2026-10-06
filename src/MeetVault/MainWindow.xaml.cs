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
        RootFrame.Navigate(typeof(LivePage));
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            if ((item.Tag as string) == "live") Nav.SelectedItem = item;
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
        catch (Exception ex)
        {
            // Navigation must never kill the app: stay put and let the user retry.
            System.Diagnostics.Debug.WriteLine($"Navigate to {tag} failed: {ex.Message}");
            sender.SelectedItem = sender.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == "dashboard");
            RootFrame.Navigate(typeof(DashboardPage));
        }
    }
}
