using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage() => InitializeComponent();
    private void Primary_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { StatusBar.Message = "Dashboard: engine wiring lands with P4; UI shell verified."; StatusBar.IsOpen = true; }
}
