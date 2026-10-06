using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeetVault.Views;

public sealed partial class SettingsPage : Page
{
    private static CredentialLockerSecretStore Locker => AppStore.Secrets;
    public static string? GeminiApiKey => Locker.Get("GeminiApiKey");
    // XAML parse fires Toggled/SelectionChanged before all fields exist.
    // Handlers stay silent until init completes.
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();

        // Real capture devices; falls back to demo label on headless machines.
        var mics = MeetVault.Core.NAudioCapture.ListDevices();
        MicPicker.ItemsSource = mics.Count > 0
            ? mics.Select(m => m.Id + ": " + m.Name).ToArray()
            : new[] { "Demo microphone (Windows default)" };
        ModelPicker.ItemsSource = new[] { "tiny (live, fast)", "base (refinement, accurate)" };
        DevicePicker.ItemsSource = new[] { "CPU", "GPU (CUDA)" };
        MicPicker.SelectedIndex = 0;
        ModelPicker.SelectedIndex = 0;
        DevicePicker.SelectedIndex = 0;

        try { LoadPersisted(); } catch { }
        _ready = true;
        Refresh();
    }

    private void Picker_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) Refresh(); }

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e) { if (_ready) Refresh(); }

    private void Diarization_Toggled(object sender, RoutedEventArgs e) { if (_ready) Refresh(); }

    private void Pin_Toggled(object sender, RoutedEventArgs e) { if (_ready) Refresh(); }

    private void Opacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { if (_ready) Refresh(); }

    private void Number_ValueChanged(object sender, NumberBoxValueChangedEventArgs e) { if (_ready) Refresh(); }

    // LostFocus rather than TextChanged: usage walks the folder, and a per-keystroke scan would stall the UI thread.
    private void Refresh_Storage(object sender, RoutedEventArgs e) { if (_ready) Refresh(); }

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
        DiarizationSwitch.IsOn = repo.GetSetting("transcription.diarization") != "off";
        PinSwitch.IsOn = repo.GetSetting("overlay.pin") != "off";
        if (double.TryParse(repo.GetSetting("overlay.opacity"), out var op)) OpacitySlider.Value = op;
        if (double.TryParse(repo.GetSetting("overlay.font"), out var font)) FontSizeBox.Value = font;
        if (double.TryParse(repo.GetSetting("overlay.lines"), out var lines)) LinesBox.Value = lines;
        StoragePathBox.Text = repo.GetSetting("storage.path") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MeetVault");
        ModelPicker.SelectedIndex = repo.GetSetting("transcription.model") == "base" ? 1 : 0;
        // Restore persisted mic index + device; clamp when hardware changed.
        if (int.TryParse(repo.GetSetting("audio.mic"), out var micIdx) && micIdx >= 0 && micIdx < MicPicker.Items.Count) MicPicker.SelectedIndex = micIdx;
        DevicePicker.SelectedIndex = repo.GetSetting("transcription.device") == "gpu" ? 1 : 0;
    }

    // One place that turns control values into the visible summary, so a section can never drift from its controls.
    private void Refresh()
    {
        if (!_ready) return;
        if (MicPicker is null || ModelPicker is null || DevicePicker is null
            || DiarizationSwitch is null || PinSwitch is null
            || OpacitySlider is null || FontSizeBox is null || LinesBox is null || StoragePathBox is null
            || AudioStatus is null || TranscriptionStatus is null || OverlayStatus is null
            || StorageStatus is null || UsageText is null || GeminiStatus is null) return;
        AudioStatus.Text = $"Capturing from {MicPicker.SelectedItem}.";
        TranscriptionStatus.Text = $"{ModelPicker.SelectedItem} on {DevicePicker.SelectedItem} \u00b7 diarization {OnOff(DiarizationSwitch.IsOn)}";
        OverlayStatus.Text = $"Keep on top {OnOff(PinSwitch.IsOn)} \u00b7 opacity {OpacitySlider.Value:P0} \u00b7 font {FontSizeBox.Value} pt \u00b7 {LinesBox.Value} line(s)";
        StorageStatus.Text = $"Transcripts will be written to {StoragePathBox.Text}.";
        UsageText.Text = DescribeUsage(StoragePathBox.Text);
        GeminiStatus.Text = GeminiApiKey is null
            ? "No key in Credential Locker."
            : "Key stored in Windows Credential Locker.";

        // Persist after the summary renders so a storage failure degrades to a warning, never a dead page.
        try
        {
            var repo = AppStore.Repo;
            repo.SetSetting("transcription.diarization", OnOff(DiarizationSwitch.IsOn));
            repo.SetSetting("overlay.pin", OnOff(PinSwitch.IsOn));
            repo.SetSetting("overlay.opacity", OpacitySlider.Value.ToString());
            repo.SetSetting("overlay.font", FontSizeBox.Value.ToString());
            repo.SetSetting("overlay.lines", LinesBox.Value.ToString());
            repo.SetSetting("storage.path", StoragePathBox.Text);
            repo.SetSetting("transcription.model", ModelPicker.SelectedIndex == 1 ? "base" : "tiny");
            var micLabel = MicPicker.SelectedItem as string ?? "0";
            var micId = micLabel.Contains(":") ? micLabel.Substring(0, micLabel.IndexOf(':')).Trim() : "0";
            repo.SetSetting("audio.mic", micId);
            repo.SetSetting("transcription.device", DevicePicker.SelectedIndex == 1 ? "gpu" : "cpu");
            _saveFailed = false;
        }
        catch (Exception ex) when (!_saveFailed)
        {
            _saveFailed = true; // report once, not on every keystroke
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = "Settings could not be saved to disk (" + DescribeStorage(ex) + "). Changes apply this session and are not persisted.";
            StatusBar.IsOpen = true;
        }
    }

    private bool _saveFailed;

    private static string DescribeStorage(Exception ex) => ex switch
    {
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 8 } => "the data folder is read-only",
        UnauthorizedAccessException => "the data folder is not writable",
        _ => ex.GetType().Name,
    };

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






