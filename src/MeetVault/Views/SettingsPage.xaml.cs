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
    // Load-time guard for the value-changed handlers: set while the ctor seeds the
    // controls from persisted settings, so a programmatic selection cannot fire a
    // persist + Refresh per control (load must not storm writes). The ctor's load path
    // still runs the persist-all write-back (PersistAll) — deliberately, and safe now
    // that every control range matches its flyout counterpart: a clamped value (a stale
    // mic index) is repaired in the store, while a value that loaded un-clamped
    // round-trips to itself. (The write-back was briefly suppressed when the Settings
    // darkness slider still had a 0.3 floor that clamped a saved 0 and then rewrote it.)
    // User-initiated changes never go through PersistAll: each handler persists only the
    // key(s) of the control the user actually touched, so touching a Settings control
    // cannot silently revert a value another surface (the overlay cogwheel flyout) wrote
    // to a different key.
    private bool _loading;

    // Where the staged speech models live; drives the picker's not-installed suffix.
    private static readonly string ModelsRoot = Path.Combine(AppContext.BaseDirectory, "models");

    public SettingsPage()
    {
        InitializeComponent();

        // Real capture devices; falls back to demo label on headless machines.
        var mics = MeetVault.Core.NAudioCapture.ListDevices();
        MicPicker.ItemsSource = mics.Count > 0
            ? mics.Select(m => m.Id + ": " + m.Name).ToArray()
            : new[] { "Demo microphone (Windows default)" };
        // Intentionally English (scope cut): model names are engine identifiers, not UI copy.
        ModelPicker.ItemsSource = MeetVault.Core.EngineCatalog.Options
            .Select(o => o.Label + (MeetVault.Core.EngineCatalog.IsAvailable(o, ModelsRoot) ? "" : " — not installed"))
            .ToArray();
        _sourceOptions.Add((Loc.T("Set.SysSrcAllApps"), ""));
        foreach (var (pid, name) in MeetVault.Core.NAudioCapture.ListAudioApps()) _sourceOptions.Add((name + " (" + pid + ")", pid.ToString()));
        SourcePicker.ItemsSource = _sourceOptions.Select(o => o.Label).ToArray();
        // Intentionally English (scope cut): log levels are diagnostic identifiers (they match the values AppLog accepts), not UI copy.
        LogLevelPicker.ItemsSource = new[] { "trace", "debug", "info", "warning", "error", "off" };
        // Language names stay endonyms (English / 日本語) — the standard convention.
        LanguagePicker.ItemsSource = new[] { Loc.T("Lang.System"), "English", "日本語" };
        MicPicker.SelectedIndex = 0;
        ModelPicker.SelectedIndex = 0;

        _loading = true;
        try { LoadPersisted(); } catch { }
        _ready = true;
        Refresh();
        PersistAll();
        _loading = false;
    }

    private readonly List<(string Label, string Value)> _sourceOptions = new();

    // A user-initiated change persists ONLY the key(s) of the control that changed, then
    // re-renders the summary; Refresh() itself writes nothing (see PersistAll for the
    // load-time write-back). This is what stops a Settings control from reverting a
    // value another surface — the overlay cogwheel flyout — just wrote to a shared key.
    private void Picker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (!_ready) return;
        if (ReferenceEquals(sender, MicPicker)) Persist("audio.mic", MicId());
        else if (ReferenceEquals(sender, ModelPicker))
        {
            var o = MeetVault.Core.EngineCatalog.Options[Math.Max(0, ModelPicker.SelectedIndex)];
            Persist("transcription.model", o.Model);
        }
        else if (ReferenceEquals(sender, LogLevelPicker))
        {
            var level = LogLevelPicker.SelectedItem as string ?? "debug";
            Persist(() => { AppStore.Repo.SetSetting("logging.level", level); MeetVault.Core.AppLog.Configure(level); });
        }
        else if (ReferenceEquals(sender, SourcePicker))
            // The source picker owns two coupled keys: audio.source is the live-format
            // value the capture path reads, audio.system.app is what seeds this picker on
            // the next load — both must follow the choice or the reload loses it.
            Persist(() => { AppStore.Repo.SetSetting("audio.source", SysSourceValue()); AppStore.Repo.SetSetting("audio.system.app", SourceValue()); });
        Refresh();
    }

    // Persists ui.language and applies the override for the NEXT launch; the InfoBar says so.
    // Unpackaged apps must use the Microsoft.Windows.Globalization variant — the
    // Windows.Globalization one throws InvalidOperationException here (L0 probe).
    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (!_ready) return;
        var value = LanguagePicker.SelectedIndex switch { 1 => "en", 2 => "ja", _ => MeetVault.Core.UiLanguage.SystemDefault };
        try
        {
            AppStore.Repo.SetSetting("ui.language", value);
        }
        catch (Exception ex)
        {
            // A read-only/full data folder must not terminate the app (the global
            // UnhandledException hook logs but never marks the event handled): report the
            // failed write instead of the restart message, and do not apply the override
            // for a setting that was not saved — this process would then render a
            // language the store does not agree with.
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = Loc.Tf("Set.SaveFailed", DescribeStorage(ex));
            StatusBar.IsOpen = true;
            return;
        }
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
                MeetVault.Core.UiLanguage.ToTag(MeetVault.Core.UiLanguage.Resolve(value, System.Globalization.CultureInfo.CurrentUICulture.Name));
        }
        catch { }
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = Loc.T("SetLangRestart");
        StatusBar.IsOpen = true;
    }

    private void Audio_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!_ready) return;
        if (ReferenceEquals(sender, MicSwitch)) Persist("audio.mic.enabled", OnOff(MicSwitch.IsOn));
        else if (ReferenceEquals(sender, SystemSwitch))
            // The toggle owns two coupled keys: turning system audio off stores the source
            // as "off" (the capture path reads it), the enabled flag records the toggle.
            Persist(() => { AppStore.Repo.SetSetting("audio.system.enabled", OnOff(SystemSwitch.IsOn)); AppStore.Repo.SetSetting("audio.source", SysSourceValue()); });
        Refresh();
    }

    private void Pin_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!_ready) return;
        Persist("overlay.pin", OnOff(PinSwitch.IsOn));
        Refresh();
    }

    private void Opacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        if (!_ready) return;
        Persist("overlay.opacity", OpacitySlider.Value.ToString());
        Refresh();
    }

    private void Number_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading) return;
        if (!_ready) return;
        if (ReferenceEquals(sender, LinesBox)) Persist("overlay.viewlines", LinesBox.Value.ToString());
        else if (ReferenceEquals(sender, FontSizeBox)) Persist("overlay.font", FontSizeBox.Value.ToString());
        Refresh();
    }

    // LostFocus rather than TextChanged: usage walks the folder, and a per-keystroke scan would stall the UI thread.
    private void Refresh_Storage(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!_ready) return;
        Persist("storage.path", StoragePathBox.Text);
        Refresh();
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        ApiKeyBox.Password = "";
        if (key.Length == 0) Locker.Clear("GeminiApiKey"); else Locker.Set("GeminiApiKey", key);
        var stored = Locker.Get("GeminiApiKey") is { Length: > 0 };
        Refresh();

        if (key.Length == 0) { StatusBar.Severity = InfoBarSeverity.Warning; StatusBar.Message = Loc.T("Set.KeyCleared"); }
        else if (stored) { StatusBar.Severity = InfoBarSeverity.Success; StatusBar.Message = Loc.T("Set.KeyStored"); }
        else { StatusBar.Severity = InfoBarSeverity.Warning; StatusBar.Message = Loc.T("Set.KeyNotStored"); }
        StatusBar.IsOpen = true;
    }

    // Persisted on every Refresh so a restart restores the exact controls; keys stay in Credential Locker, never here.
    private void LoadPersisted()
    {
        var repo = AppStore.Repo;
        LanguagePicker.SelectedIndex = (repo.GetSetting("ui.language") ?? "system") switch { "en" => 1, "ja" => 2, _ => 0 };
        PinSwitch.IsOn = repo.GetSetting("overlay.pin") != "off";
        if (double.TryParse(repo.GetSetting("overlay.opacity"), out var op)) OpacitySlider.Value = op;
        if (double.TryParse(repo.GetSetting("overlay.font"), out var font)) FontSizeBox.Value = font;
        // The overlay's VISIBLE line count: one key drives both the cogwheel flyout slider
        // and this row (overlay.viewlines). overlay.lines is the retired key; an install
        // that saved it keeps its value — read the new key first, fall back to the old one
        // once, and persist the fallback forward so the old row stops being read.
        var viewLines = repo.GetSetting("overlay.viewlines");
        if (string.IsNullOrEmpty(viewLines))
        {
            viewLines = repo.GetSetting("overlay.lines");
            if (!string.IsNullOrEmpty(viewLines)) repo.SetSetting("overlay.viewlines", viewLines);
        }
        if (double.TryParse(viewLines, out var lines)) LinesBox.Value = lines;
        StoragePathBox.Text = repo.GetSetting("storage.path") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MeetVault");
        ModelPicker.SelectedIndex = MeetVault.Core.EngineCatalog.IndexOf(repo.GetSetting("transcription.model"));
        // Restore persisted mic index; clamp when hardware changed.
        if (int.TryParse(repo.GetSetting("audio.mic"), out var micIdx) && micIdx >= 0 && micIdx < MicPicker.Items.Count) MicPicker.SelectedIndex = micIdx;
        var logLevel = (repo.GetSetting("logging.level") ?? "debug").Trim().ToLowerInvariant();
        LogLevelPicker.SelectedIndex = Array.IndexOf(new[] { "trace", "debug", "info", "warning", "error", "off" }, logLevel) is var li && li >= 0 ? li : 1;
        MicSwitch.IsOn = repo.GetSetting("audio.mic.enabled") != "off";
        SystemSwitch.IsOn = repo.GetSetting("audio.system.enabled") != "off";
        var app = repo.GetSetting("audio.system.app") ?? "";
        var si = _sourceOptions.FindIndex(o => o.Value == app);
        SourcePicker.SelectedIndex = si >= 0 ? si : 0;
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(MeetVault.Core.AppLog.Path);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
        }
        catch (Exception ex) { Warn(Loc.Tf("Set.OpenLogFailed", DescribeStorage(ex))); }
    }

    private void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(MeetVault.Core.AppLog.Path))
            {
                var text = File.ReadAllText(MeetVault.Core.AppLog.Path);
                var tail = text.Length > 4000 ? text[^4000..] : text;
                LogStatus.Text = tail;
            }
            else LogStatus.Text = Loc.Tf("Set.NoLogYet", MeetVault.Core.AppLog.Path);
        }
        catch (Exception ex) { Warn(Loc.Tf("Set.ReadLogFailed", DescribeStorage(ex))); }
    }

    private void Warn(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Warning;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    // One place that turns control values into the visible summary, so a section can never
    // drift from its controls. Render only: persistence is per-control in the handlers,
    // plus the ctor's PersistAll write-back.
    private void Refresh()
    {
        if (!_ready) return;
        if (MicPicker is null || ModelPicker is null
            || PinSwitch is null
            || OpacitySlider is null || FontSizeBox is null || LinesBox is null || StoragePathBox is null
            || AudioStatus is null || TranscriptionStatus is null || OverlayStatus is null
            || StorageStatus is null || UsageText is null || GeminiStatus is null) return;
        var micOn = MicSwitch.IsOn;
        var sysOn = SystemSwitch.IsOn;
        var sourceLabel = SourcePicker.SelectedItem as string ?? Loc.T("Set.SysSrcAllApps");
        AudioStatus.Text = (micOn, sysOn) switch
        {
            (true, true) => Loc.Tf("Set.AudioBoth", MicPicker.SelectedItem, sourceLabel),
            (true, false) => Loc.Tf("Set.AudioMicOnly", MicPicker.SelectedItem),
            (false, true) => Loc.Tf("Set.AudioSysOnly", sourceLabel),
            _ => Loc.T("Set.AudioNone"),
        };
        TranscriptionStatus.Text = Loc.Tf("Set.TranscriptionStatus", ModelPicker.SelectedItem);
        var logLevel = LogLevelPicker.SelectedItem as string ?? "debug";
        LogStatus.Text = Loc.Tf("Set.LogWriting", logLevel, MeetVault.Core.AppLog.Path);
        OverlayStatus.Text = Loc.Tf("Set.OverlayStatus", OnOffDisplay(PinSwitch.IsOn), OpacitySlider.Value.ToString("P0"), FontSizeBox.Value, LinesBox.Value);
        StorageStatus.Text = Loc.Tf("Set.StorageStatus", StoragePathBox.Text);
        UsageText.Text = DescribeUsage(StoragePathBox.Text);
        GeminiStatus.Text = GeminiApiKey is null
            ? Loc.T("Set.GeminiNoKey")
            : Loc.T("Set.KeyStored");
    }

    // Load-time write-back, ctor only: persists every control's current value once, after
    // the controls were seeded, so a value they had to clamp is repaired in the store — a
    // stored mic index past the device list falls to the picker's first entry — while a
    // value that loaded un-clamped round-trips to itself (every range matches its flyout
    // counterpart, so nothing rewrites it). User-initiated changes never call this; each
    // handler persists only its own key(s).
    private void PersistAll()
    {
        Persist(() =>
        {
            var repo = AppStore.Repo;
            repo.SetSetting("overlay.pin", OnOff(PinSwitch.IsOn));
            repo.SetSetting("overlay.opacity", OpacitySlider.Value.ToString());
            repo.SetSetting("overlay.font", FontSizeBox.Value.ToString());
            repo.SetSetting("overlay.viewlines", LinesBox.Value.ToString());
            repo.SetSetting("storage.path", StoragePathBox.Text);
            repo.SetSetting("transcription.model", MeetVault.Core.EngineCatalog.Options[Math.Max(0, ModelPicker.SelectedIndex)].Model);
            repo.SetSetting("audio.mic", MicId());
            repo.SetSetting("audio.source", SysSourceValue());
            repo.SetSetting("audio.mic.enabled", OnOff(MicSwitch.IsOn));
            repo.SetSetting("audio.system.enabled", OnOff(SystemSwitch.IsOn));
            repo.SetSetting("audio.system.app", SourceValue());
            var logLevel = LogLevelPicker.SelectedItem as string ?? "debug";
            repo.SetSetting("logging.level", logLevel);
            MeetVault.Core.AppLog.Configure(logLevel);
        });
    }

    // One settings write for the control the user changed; a read-only/full data folder
    // degrades to a warning (with DescribeStorage(ex)), never a dead page.
    private void Persist(Action write)
    {
        try
        {
            write();
            _saveFailed = false;
        }
        catch (Exception ex) when (!_saveFailed)
        {
            _saveFailed = true; // report once, not on every keystroke
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = Loc.Tf("Set.SaveFailed", DescribeStorage(ex));
            StatusBar.IsOpen = true;
        }
    }

    private void Persist(string key, string value) => Persist(() => AppStore.Repo.SetSetting(key, value));

    // Current control values in their persisted formats, shared by PersistAll and the
    // per-control handlers so the two can never drift.
    private string MicId()
    {
        var micLabel = MicPicker.SelectedItem as string ?? "0";
        return micLabel.Contains(":") ? micLabel.Substring(0, micLabel.IndexOf(':')).Trim() : "0";
    }

    private string SourceValue() => SourcePicker.SelectedIndex >= 0 && SourcePicker.SelectedIndex < _sourceOptions.Count ? _sourceOptions[SourcePicker.SelectedIndex].Value : "";

    // audio.source's stored value for the current controls: "off" while system audio is
    // off, otherwise the chosen app (or "all").
    private string SysSourceValue() => SystemSwitch.IsOn ? (string.IsNullOrEmpty(SourceValue()) ? "all" : SourceValue()) : "off";

    private bool _saveFailed;

    private static string DescribeStorage(Exception ex) => ex switch
    {
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 8 } => Loc.T("Set.ErrReadOnly"),
        UnauthorizedAccessException => Loc.T("Set.ErrNotWritable"),
        _ => Loc.T("Set.ErrNotWritten"),
    };

    // Persisted settings values stay English ("on"/"off"); the display helper localizes.
    private static string OnOff(bool value) => value ? "on" : "off";
    private static string OnOffDisplay(bool value) => value ? Loc.T("Set.On") : Loc.T("Set.Off");

    private static string DescribeUsage(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Loc.T("Set.UsageNone");
        try
        {
            var bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            return Loc.Tf("Set.UsageMb", (bytes / 1024.0 / 1024.0).ToString("0.##"), folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Loc.T("Set.UsageUnavailable");
        }
    }
}
