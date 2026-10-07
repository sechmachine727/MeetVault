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
        // Pin the app's own onnxruntime before the WindowsAppRuntime framework copy
        // hijacks the "onnxruntime.dll" base name: sherpa-onnx binds by name and
        // access-violates against the mismatched WASDK build.
        try
        {
            var nativeOnnx = System.IO.Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");
            if (System.IO.File.Exists(nativeOnnx))
                System.Runtime.InteropServices.NativeLibrary.Load(nativeOnnx);
            else
                LogStartup("onnxruntime.dll not found beside the exe; sherpa may fail at engine init.");
        }
        catch (Exception ex) { LogStartup("onnxruntime pin failed: " + ex.Message); }
        // Configure the app log from Settings (default level: debug) before anything logs.
        try { MeetVault.Core.AppLog.Configure(Views.AppStore.Repo.GetSetting("logging.level") ?? "debug"); }
        catch { MeetVault.Core.AppLog.Configure("debug"); }
        MeetVault.Core.AppLog.Info("app start; log level=" + MeetVault.Core.AppLog.LevelName(MeetVault.Core.AppLog.Parse(Views.AppStore.Repo.GetSetting("logging.level"))));
        // Touch the GPU probe so its capability report is in the log from startup, not only
        // once Settings is opened. First access runs the probe's static constructor.
        _ = MeetVault.Core.GpuProbe.ApiSummary();
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
