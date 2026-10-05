using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeetVault.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly CredentialLockerSecretStore Locker = new();
    public static string? GeminiApiKey => Locker.Get("GeminiApiKey");

    public SettingsPage()
    {
        InitializeComponent();

        MicPicker.ItemsSource = new[] { "Demo microphone (Windows default)" };
        ModelPicker.ItemsSource = new[] { "base.en-q5_1" };
        DevicePicker.ItemsSource = new[] { "CPU" };
        MicPicker.SelectedIndex = 0;
        ModelPicker.SelectedIndex = 0;
        DevicePicker.SelectedIndex = 0;

        StoragePathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MeetVault");
        Refresh();
    }

    private void Picker_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Diarization_Toggled(object sender, RoutedEventArgs e) => Refresh();

    private void Gender_Toggled(object sender, RoutedEventArgs e) => Refresh();

    private void Pin_Toggled(object sender, RoutedEventArgs e) => Refresh();

    private void Opacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Refresh();

    private void Number_ValueChanged(object sender, NumberBoxValueChangedEventArgs e) => Refresh();

    // LostFocus rather than TextChanged: usage walks the folder, and a per-keystroke scan would stall the UI thread.
    private void Refresh_Storage(object sender, RoutedEventArgs e) => Refresh();

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        ApiKeyBox.Password = "";
        if (key.Length == 0) Locker.Clear("GeminiApiKey"); else Locker.Set("GeminiApiKey", key);
        Refresh();

        StatusBar.Severity = key.Length == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        StatusBar.Message = key.Length == 0
            ? "No key entered, so the in-memory key was cleared."
            : "Key stored in Windows Credential Locker.";
        StatusBar.IsOpen = true;
    }

    // One place that turns control values into the visible summary, so a section can never drift from its controls.
    private void Refresh()
    {
        AudioStatus.Text = $"Capturing from {MicPicker.SelectedItem} via {AppSourceBox.Text}.";
        TranscriptionStatus.Text = $"{ModelPicker.SelectedItem} on {DevicePicker.SelectedItem} \u00b7 diarization {OnOff(DiarizationSwitch.IsOn)} \u00b7 gender labels {OnOff(GenderSwitch.IsOn)}";
        OverlayStatus.Text = $"Keep on top {OnOff(PinSwitch.IsOn)} \u00b7 opacity {OpacitySlider.Value:P0} \u00b7 font {FontSizeBox.Value} pt \u00b7 {LinesBox.Value} line(s)";
        StorageStatus.Text = $"Transcripts will be written to {StoragePathBox.Text}.";
        UsageText.Text = DescribeUsage(StoragePathBox.Text);
        GeminiStatus.Text = GeminiApiKey is null
            ? "No key in Credential Locker."
            : "Key stored in Windows Credential Locker.";
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string DescribeUsage(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return "Usage: 0 B \u2014 folder does not exist yet.";
        try
        {
            var bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            return $"Usage: {bytes / 1024.0 / 1024.0:0.##} MB in {folder}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Usage: unavailable \u2014 {ex.Message}";
        }
    }
}

