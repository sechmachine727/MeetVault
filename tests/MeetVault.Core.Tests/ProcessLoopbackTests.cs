using System.Runtime.InteropServices;
using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Exercises the process-loopback activation path. It must either activate or fail
// gracefully (COMException/TimeoutException) without hanging or crashing; the app falls
// back to system-wide loopback when activation fails.
public sealed class ProcessLoopbackTests
{
    [Fact]
    public void ProcessLoopback_ActivatesOrFailsGracefully()
    {
        var cap = new ProcessLoopbackCapture((uint)Environment.ProcessId);
        try
        {
            cap.StartRecording();
            AppLog.Info("test: process loopback activated");
            Thread.Sleep(300);
            cap.StopRecording();
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or InvalidOperationException)
        {
            AppLog.Warn("test: process loopback unavailable :: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { cap.Dispose(); }
    }
}

