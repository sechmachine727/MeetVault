using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace MeetVault.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        RecentMeetings.ItemsSource = DemoMeetings.Cards;
        EmptyState.Visibility = DemoMeetings.Cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is MeetVault.MainWindow main)
            main.NavigateToLive();
        else
        {
            StatusBar.Message = "Cannot open Live from here. Use the Live item in the navigation pane.";
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.IsOpen = true;
        }
    }
}

