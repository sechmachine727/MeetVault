using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MeetVault;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    // Startup diagnostics go to the same log the nav errors use.
    private static void LogStartup(string message)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "MeetVault");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "nav-errors.log"), DateTimeOffset.Now.ToString("o") + " startup: " + message + global::System.Environment.NewLine);
        }
        catch { }
    }

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        // Display language: ui.language (en|ja) wins; otherwise the Windows language list
        // decides. The UI language (resource lookup) always follows the resolved tag; the
        // thread CULTURE (date/number formats) follows it only for an explicit en/ja
        // choice — in system mode the machine's own formats stay (a de-DE/en-GB/vi system
        // is not forced to en-US formats; a ja-JP system still resolves ja-JP). Must run
        // before any XAML or resource-map use — this is the first statement in the ctor
        // for that reason. Restart-to-apply is the documented behavior.
        // Unpackaged apps must use Microsoft.Windows.Globalization.ApplicationLanguages:
        // the Windows.Globalization variant throws InvalidOperationException here (L0 probe).
        try
        {
            var langSetting = Views.AppStore.Repo.GetSetting("ui.language");
            var resolved = MeetVault.Core.UiLanguage.Resolve(langSetting, System.Globalization.CultureInfo.CurrentUICulture.Name);
            var tag = MeetVault.Core.UiLanguage.ToTag(resolved);
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = tag;
            var culture = new System.Globalization.CultureInfo(tag);
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            var explicitChoice = MeetVault.Core.UiLanguage.IsExplicit(langSetting);
            if (explicitChoice)
                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            LogStartup("ui language: setting=" + (langSetting ?? "(none)") + " resolved=" + tag + " culture=" + (explicitChoice ? tag : "(system)"));
        }
        catch (Exception ex) { LogStartup("ui language init failed: " + ex.Message); }
        // Configure the app log from Settings (default level: debug) before anything logs.
        try { MeetVault.Core.AppLog.Configure(Views.AppStore.Repo.GetSetting("logging.level") ?? "debug"); }
        catch { MeetVault.Core.AppLog.Configure("debug"); }
        MeetVault.Core.AppLog.Info("app start; log level=" + MeetVault.Core.AppLog.LevelName(MeetVault.Core.AppLog.Parse(Views.AppStore.Repo.GetSetting("logging.level"))));
        // Crash prevention (AGENTS constraint): load the app's OWN onnxruntime.dll before
        // anything else can. WindowsAppSDK.ML ships a same-named native module — if its copy
        // won the load, the OnnxRuntime P/Invokes would bind to the wrong binary. The langid
        // model makes this app the consumer, so the app-directory copy is pinned here.
        try
        {
            var ortPath = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");
            if (File.Exists(ortPath)) System.Runtime.InteropServices.NativeLibrary.Load(ortPath);
        }
        catch (Exception ex) { LogStartup("onnxruntime pin failed: " + ex.Message); }
        InitializeComponent();
        // ponytail: last-resort crash log. Unhandled UI exceptions otherwise vanish
        // in the unpackaged exe with no debugger attached.
        UnhandledException += (s, e) =>
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "MeetVault");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    DateTimeOffset.Now.ToString("o") + " " + e.Exception.GetType().Name + ": " + e.Exception.Message
                    + " STACK:" + (e.Exception.StackTrace ?? string.Empty) + global::System.Environment.NewLine);
            }
            catch { }
            MeetVault.Core.AppLog.Error("unhandled UI exception", e.Exception);
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }
}
