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
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        RootFrame.Navigate(tag switch
        {
            "live" => typeof(LivePage),
            "meetings" => typeof(MeetingsPage),
            "details" => typeof(DetailsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        });
    }
}

