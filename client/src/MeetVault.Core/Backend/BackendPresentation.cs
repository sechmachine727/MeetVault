namespace MeetVault.Core.Backend;

// Pure display helpers shared by the Details section and the Backend page. They return
// presentation DATA (numbers, resw keys, plain strings) — localized text is applied by the
// views via Loc, so this stays in Core and unit-testable.
public static class BackendPresentation
{
    public static int Percent(double progress) => Math.Clamp((int)Math.Round(progress * 100), 0, 100);

    public static string StatusKey(string serverStatus) => serverStatus switch
    {
        "uploaded" => "Back.State.Uploaded",
        "sending" => "Back.State.Sending",
        "processing" => "Back.State.Processing",
        "ready" => "Back.State.Ready",
        "failed" => "Back.State.Failed",
        "deleted" => "Back.State.Deleted",
        "" => "Back.State.None",
        _ => "Back.State.Unknown",
    };

    public static string StepsSummary(IReadOnlyDictionary<string, string>? steps)
    {
        if (steps is null || steps.Count == 0) return "";
        var order = new[] { "media_probe", "transcribe", "diarize", "align", "analyze", "qa" };
        var parts = new List<string>();
        foreach (var name in order)
            if (steps.TryGetValue(name, out var state) && !string.IsNullOrEmpty(state))
                parts.Add(name + ": " + state);
        foreach (var pair in steps)
            if (!order.Contains(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                parts.Add(pair.Key + ": " + pair.Value);
        return string.Join(" · ", parts);
    }

    // "[mm:ss] Speaker" / "[hh:mm:ss] " for the server transcript rows.
    public static string SpeakerLine(long startMs, string speaker)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
        var stamp = time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss") : time.ToString(@"mm\:ss");
        return "[" + stamp + "] " + speaker;
    }

    public static bool IsStale(IReadOnlyList<string> staleKinds, string kind) => staleKinds.Contains(kind);
}
