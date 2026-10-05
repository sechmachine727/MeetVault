using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();
    private void Primary_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { StatusBar.Message = "Settings: engine wiring lands with P4; UI shell verified."; StatusBar.IsOpen = true; }
}
