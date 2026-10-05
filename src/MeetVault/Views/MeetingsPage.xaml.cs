using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class MeetingsPage : Page
{
    public MeetingsPage() => InitializeComponent();
    private void Primary_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { StatusBar.Message = "Meetings: engine wiring lands with P4; UI shell verified."; StatusBar.IsOpen = true; }
}
