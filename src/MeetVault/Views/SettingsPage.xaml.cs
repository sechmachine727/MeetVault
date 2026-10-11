using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Globalization;

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
        // Index 0 is Auto-detect (the default mode for fresh installs); the rest are
        // endonyms, standard convention. The size rows rebuild per language below.
        SpeechLangPicker.ItemsSource = new[] { Loc.T("Lang.Auto") }
            .Concat(MeetVault.Core.EngineCatalog.Languages.Select(l => l.Endonym)).ToArray();
        _sourceOptions.Add((Loc.T("Set.SysSrcAllApps"), ""));
        foreach (var (pid, name) in MeetVault.Core.NAudioCapture.ListAudioApps()) _sourceOptions.Add((name + " (" + pid + ")", pid.ToString()));
        SourcePicker.ItemsSource = _sourceOptions.Select(o => o.Label).ToArray();
        // Intentionally English (scope cut): log levels are diagnostic identifiers (they match the values AppLog accepts), not UI copy.
        LogLevelPicker.ItemsSource = new[] { "trace", "debug", "info", "warning", "error", "off" };
        // Language names stay endonyms (English / 日本語) — the standard convention.
        LanguagePicker.ItemsSource = new[] { Loc.T("Lang.System"), "English", "日本語" };
        MicPicker.SelectedIndex = 0;
        SpeechLangPicker.SelectedIndex = 0;
        RefreshSizeRows();

        _loading = true;
        try { LoadPersisted(); } catch { }
        _ready = true;
        Refresh();
        PersistAll();
        _loading = false;
    }

    private readonly List<(string Label, string Value)> _sourceOptions = new();

    // One row of the model-size dropdown. Enabled=false marks a tier this language has no
    // artifact for. Tier names and suffixes go through Loc like the Set.Size*Details captions
    // (tier words stay Latin in ja there too).
    public sealed record SizeRow(string Tier, string Label, bool Enabled);

    private bool _rebuildingSizes;

    // --- Screen recording (E4) ---

    private readonly List<MeetVault.Core.CaptureSource> _screenSources = new();
    private bool _suppressScreenEvent;

    private void RefreshScreenSources()
    {
        _screenSources.Clear();
        _screenSources.AddRange(MeetVault.Core.CaptureSourceResolver.Displays());
        _screenSources.AddRange(MeetVault.Core.CaptureSourceResolver.Windows());
        _suppressScreenEvent = true;
        try
        {
            ScreenSourcePicker.Items.Clear();
            foreach (var s in _screenSources) ScreenSourcePicker.Items.Add(s.Label);
            var stored = (AppStore.Repo.GetSetting("screen.source") ?? "").Trim();
            var idx = 0;
            if (stored.Length > 0)
                for (var i = 0; i < _screenSources.Count; i++)
                    if (string.Equals(MeetVault.Core.CaptureSourceResolver.Format(_screenSources[i]), stored, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            ScreenSourcePicker.SelectedIndex = idx;
        }
        finally { _suppressScreenEvent = false; }
    }

    private void Screen_Toggled(object sender, RoutedEventArgs e)
    {
        // IsOn="True" fires Toggled during InitializeComponent, before LoadPersisted reads
        // the key — without the guard a saved "false" is clobbered on every Settings open
        // (review r5). Persist keeps a read-only store non-fatal.
        if (!_ready || _suppressScreenEvent) return;
        Persist("screen.enabled", ScreenSwitch.IsOn ? "true" : "false");
    }

    private void ScreenSourcePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressScreenEvent || ScreenSourcePicker.SelectedIndex < 0 || ScreenSourcePicker.SelectedIndex >= _screenSources.Count) return;
        Persist("screen.source", MeetVault.Core.CaptureSourceResolver.Format(_screenSources[ScreenSourcePicker.SelectedIndex]));
    }

    private string CurrentLanguage()
    {
        var idx = Math.Max(0, SpeechLangPicker.SelectedIndex);
        return idx == 0
            ? "auto"
            : MeetVault.Core.EngineCatalog.Languages[Math.Clamp(idx - 1, 0, MeetVault.Core.EngineCatalog.Languages.Length - 1)].Code;
    }

    private string SizeTier() => (SizePicker.SelectedItem as SizeRow)?.Tier ?? "small";

    private static string FormatMb(int mb) =>
        mb < 1000 ? "~" + mb + " MB" : "~" + (mb / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB";

    // Sizes the current language actually offers; keep the selected tier when still valid.
    private void RefreshSizeRows()
    {
        // Auto mode shows the tiers English offers (the live resolver applies the chosen
        // size where a language has it and falls back to small otherwise).
        var lang = CurrentLanguage();
        if (lang == "auto") lang = "en";
        var keep = SizeTier();
        var rows = MeetVault.Core.EngineCatalog.Tiers.Select(t =>
        {
            var option = MeetVault.Core.EngineCatalog.Find(lang, t);
            var label = t switch
            {
                "medium" => Loc.T("Set.SizeMedium"),
                "large" => Loc.T("Set.SizeLarge"),
                _ => Loc.T("Set.SizeSmall"),
            };
            if (option is null) return new SizeRow(t, label + " " + Loc.T("Set.SizeRowUnavailable"), false);
            return new SizeRow(t, label + (MeetVault.Core.EngineCatalog.IsAvailable(option, ModelsRoot) ? "" : " " + Loc.T("Set.SizeRowNotInstalled")), true);
        }).ToArray();
        _rebuildingSizes = true;
        try
        {
            SizePicker.ItemsSource = rows;
            var idx = Array.FindIndex(rows, r => r.Tier == keep && r.Enabled);
            SizePicker.SelectedIndex = idx >= 0 ? idx : 0;
        }
        finally { _rebuildingSizes = false; }
    }

    // WinUI style setters cannot carry a {Binding} (an ItemContainerStyle IsEnabled setter
    // is inert — verified via UIA en=True on the disabled row), and ComboBox is not a
    // ListViewBase so ContainerContentChanging is unavailable; this is the event that can
    // reach the container. A tier without an artifact for the current language stays
    // visible but unselectable.
    private void SizePicker_DropDownOpened(object sender, object e)
    {
        ApplySizeRowEnabled();
        // Containers are not always realized at the moment the popup opens; re-apply on the
        // next dispatcher turn so the rows carry their state before any interaction.
        SizePicker.DispatcherQueue.TryEnqueue(ApplySizeRowEnabled);
    }

    private void ApplySizeRowEnabled()
    {
        for (var i = 0; i < SizePicker.Items.Count; i++)
        {
            if (SizePicker.ContainerFromIndex(i) is ComboBoxItem item && SizePicker.Items[i] is SizeRow row)
                item.IsEnabled = row.Enabled;
        }
    }

    // A user-initiated change persists ONLY the key(s) of the control that changed, then
    // re-renders the summary; Refresh() itself writes nothing (see PersistAll for the
    // load-time write-back). This is what stops a Settings control from reverting a
    // value another surface — the overlay cogwheel flyout — just wrote to a shared key.
    private void Picker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (!_ready) return;
        if (_rebuildingSizes) return;
        if (ReferenceEquals(sender, MicPicker)) Persist("audio.mic", MicId());
        else if (ReferenceEquals(sender, SpeechLangPicker))
        {
            // Coupled write (SourcePicker precedent): the language owns which size tiers
            // exist, so switching can force a size change — persist both keys.
            RefreshSizeRows();
            Persist(() => { AppStore.Repo.SetSetting("transcription.language", CurrentLanguage()); AppStore.Repo.SetSetting("transcription.size", SizeTier()); });
        }
        else if (ReferenceEquals(sender, SizePicker))
        {
            if (SizePicker.SelectedItem is SizeRow row && !row.Enabled)
            {
                // Belt and braces: an unavailable tier must never become the selection, even
                // if some input path bypasses the row's disabled state. Restore the tier the
                // store still holds (nothing was persisted on this path), then re-render so
                // the caption/status can never describe the rejected pick.
                _rebuildingSizes = true;
                try
                {
                    var rows = SizePicker.ItemsSource as SizeRow[] ?? Array.Empty<SizeRow>();
                    var keep = (AppStore.Repo.GetSetting("transcription.size") ?? "").Trim().ToLowerInvariant();
                    var idx = Array.FindIndex(rows, r => r.Tier == keep && r.Enabled);
                    if (idx < 0) idx = Array.FindIndex(rows, r => r.Enabled);
                    if (idx >= 0) SizePicker.SelectedIndex = idx;
                }
                finally { _rebuildingSizes = false; }
                Refresh();
                return;
            }
            Persist("transcription.size", SizeTier());
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
        Persist("overlay.opacity", OpacitySlider.Value.ToString(CultureInfo.InvariantCulture));
        Refresh();
    }

    private void Number_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading) return;
        if (!_ready) return;
        if (ReferenceEquals(sender, LinesBox)) Persist("overlay.viewlines", LinesBox.Value.ToString(CultureInfo.InvariantCulture));
        else if (ReferenceEquals(sender, FontSizeBox)) Persist("overlay.font", FontSizeBox.Value.ToString(CultureInfo.InvariantCulture));
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
        // Numeric settings round-trip with the INVARIANT culture: the UI language choice
        // changes DefaultThreadCurrentCulture, and "0,2" parsed as en-US would become 2
        // (review r13).
        if (double.TryParse(repo.GetSetting("overlay.opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var op)) OpacitySlider.Value = op;
        if (double.TryParse(repo.GetSetting("overlay.font"), NumberStyles.Float, CultureInfo.InvariantCulture, out var font)) FontSizeBox.Value = font;
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
        if (double.TryParse(viewLines, NumberStyles.Float, CultureInfo.InvariantCulture, out var lines)) LinesBox.Value = lines;
        StoragePathBox.Text = repo.GetSetting("storage.path") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MeetVault");
        // Speech language × size; one-time forward-migration of the retired single model
        // key (the overlay.viewlines migration above is the precedent).
        var langCode = repo.GetSetting("transcription.language");
        var sizeTier = repo.GetSetting("transcription.size");
        if (string.IsNullOrEmpty(langCode) && string.IsNullOrEmpty(sizeTier))
        {
            // Fresh install with no legacy model: Auto-detect is the default mode. An
            // install that still names a retired transcription.model migrates to its pair.
            (langCode, sizeTier) = string.IsNullOrEmpty(repo.GetSetting("transcription.model"))
                ? ("auto", "small")
                : MeetVault.Core.EngineCatalog.MigrateLegacy(repo.GetSetting("transcription.model"));
            repo.SetSetting("transcription.language", langCode);
            repo.SetSetting("transcription.size", sizeTier);
            // The retired key has been consumed; deleting it keeps it out of every later
            // fallback chain (the auto resolver must never resurrect it).
            repo.DeleteSetting("transcription.model");
        }
        langCode = (langCode ?? "").Trim().ToLowerInvariant();
        SpeechLangPicker.SelectedIndex = langCode == "auto"
            ? 0
            : Math.Max(0, Array.FindIndex(MeetVault.Core.EngineCatalog.Languages, l => l.Code == langCode) + 1);

        // Screen recording (E4): the global default for new meetings + the source picker.
        _suppressScreenEvent = true;
        ScreenSwitch.IsOn = repo.GetSetting("screen.enabled") != "false";
        _suppressScreenEvent = false;
        RefreshScreenSources();
        RefreshSizeRows();
        var sizeIdx = Array.FindIndex(SizePicker.ItemsSource as SizeRow[] ?? Array.Empty<SizeRow>(),
            r => r.Tier == (sizeTier ?? "").Trim().ToLowerInvariant() && r.Enabled);
        SizePicker.SelectedIndex = sizeIdx >= 0 ? sizeIdx : 0;
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
        if (MicPicker is null || SpeechLangPicker is null || SizePicker is null || ModelDetails is null
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
        var tier = SizeTier();
        ModelDetails.Text = tier switch
        {
            "medium" => Loc.T("Set.SizeMediumDetails"),
            "large" => Loc.T("Set.SizeLargeDetails"),
            _ => Loc.T("Set.SizeSmallDetails"),
        };
        var statusLang = CurrentLanguage();
        var option = MeetVault.Core.EngineCatalog.Find(statusLang == "auto" ? "en" : statusLang, tier);
        if (option is not null)
        {
            var availability = MeetVault.Core.EngineCatalog.IsAvailable(option, ModelsRoot)
                ? Loc.T("Set.ModelInstalled")
                : Loc.T("Set.ModelNotInstalled");
            TranscriptionStatus.Text = Loc.Tf("Set.TranscriptionStatus", option.Label, FormatMb(option.DownloadMb), availability);
            if (statusLang == "auto")
            {
                TranscriptionStatus.Text += " · " + Loc.T("Set.SizeAutoNote");
                if (!MeetVault.Core.LangIdModel.IsAvailable(ModelsRoot))
                    TranscriptionStatus.Text += " · " + Loc.T("Set.LangIdMissing");
            }
        }
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
            repo.SetSetting("overlay.opacity", OpacitySlider.Value.ToString(CultureInfo.InvariantCulture));
            repo.SetSetting("overlay.font", FontSizeBox.Value.ToString(CultureInfo.InvariantCulture));
            repo.SetSetting("overlay.viewlines", LinesBox.Value.ToString(CultureInfo.InvariantCulture));
            repo.SetSetting("storage.path", StoragePathBox.Text);
            repo.SetSetting("transcription.language", CurrentLanguage());
            repo.SetSetting("transcription.size", SizeTier());
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
