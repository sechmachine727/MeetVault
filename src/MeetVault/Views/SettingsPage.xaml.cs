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

        LoadPersisted();
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

    // Persisted on every Refresh so a restart restores the exact controls; keys stay in Credential Locker, never here.
    private void LoadPersisted()
    {
        var repo = AppStore.Repo;
        AppSourceBox.Text = repo.GetSetting("audio.source") ?? "";
        DiarizationSwitch.IsOn = repo.GetSetting("transcription.diarization") != "off";
        GenderSwitch.IsOn = repo.GetSetting("transcription.gender") != "off";
        PinSwitch.IsOn = repo.GetSetting("overlay.pin") != "off";
        if (double.TryParse(repo.GetSetting("overlay.opacity"), out var op)) OpacitySlider.Value = op;
        if (double.TryParse(repo.GetSetting("overlay.font"), out var font)) FontSizeBox.Value = font;
        if (double.TryParse(repo.GetSetting("overlay.lines"), out var lines)) LinesBox.Value = lines;
        StoragePathBox.Text = repo.GetSetting("storage.path") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MeetVault");
    }

    // One place that turns control values into the visible summary, so a section can never drift from its controls.
    private void Refresh()
    {
        var repo = AppStore.Repo;
        repo.SetSetting("audio.source", AppSourceBox.Text);
        repo.SetSetting("transcription.diarization", OnOff(DiarizationSwitch.IsOn));
        repo.SetSetting("transcription.gender", OnOff(GenderSwitch.IsOn));
        repo.SetSetting("overlay.pin", OnOff(PinSwitch.IsOn));
        repo.SetSetting("overlay.opacity", OpacitySlider.Value.ToString());
        repo.SetSetting("overlay.font", FontSizeBox.Value.ToString());
        repo.SetSetting("overlay.lines", LinesBox.Value.ToString());
        repo.SetSetting("storage.path", StoragePathBox.Text);
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


