using System.Text.Json;

namespace MeetVault.Core.Backend;

// Flattens the analysis bundle's loosely-typed document payloads into UI-ready records.
// Every reader is defensive: an absent or odd field yields an empty value, never a throw.
public static class BackendAnalysisParser
{
    public static BackendBundle Flatten(BackendBundleRaw raw) => new(
        raw.MeetingId, raw.TranscriptId, raw.Run,
        ParseSummary(raw.Summary),
        ParseAgenda(raw.Agenda),
        raw.Sections ?? new List<BackendSection>(),
        (raw.SectionSummaries ?? new List<BackendDocumentRaw>()).Select(ParseSectionSummary).Where(x => x is not null).Select(x => x!).ToList(),
        ParseDecisions(raw.Decisions),
        ParseActions(raw.ActionItems),
        (raw.Qa ?? new List<BackendDocumentRaw>()).Select(ParseQa).Where(x => x is not null).Select(x => x!).ToList(),
        raw.StaleKinds ?? new List<string>(),
        raw.Mock);

    private static string Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long? Long(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : null;

    private static List<string> StrList(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").Where(x => x.Length > 0).ToList();
    }

    private static BackendSummaryView? ParseSummary(JsonElement? raw)
    {
        if (raw is not { } element || element.ValueKind != JsonValueKind.Object) return null;
        var bullets = new List<string>();
        if (element.TryGetProperty("bullets", out var b) && b.ValueKind == JsonValueKind.Array)
            bullets.AddRange(b.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
        return new BackendSummaryView(Str(element, "text"), bullets);
    }

    private static List<BackendAgendaItem> ParseAgenda(JsonElement? raw)
    {
        var items = new List<BackendAgendaItem>();
        if (raw is not { } element || !element.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array) return items;
        foreach (var item in array.EnumerateArray())
            items.Add(new BackendAgendaItem(Str(item, "title"), Str(item, "topic"), Long(item, "start_ms") ?? 0, Long(item, "end_ms") ?? 0));
        return items;
    }

    private static List<BackendDecision> ParseDecisions(JsonElement? raw)
    {
        var items = new List<BackendDecision>();
        if (raw is not { } element || !element.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array) return items;
        foreach (var item in array.EnumerateArray())
            items.Add(new BackendDecision(Str(item, "text"), StrList(item, "decided_by"), Long(item, "at_ms"), StrList(item, "segment_ids")));
        return items;
    }

    private static List<BackendActionItem> ParseActions(JsonElement? raw)
    {
        var items = new List<BackendActionItem>();
        if (raw is not { } element || !element.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array) return items;
        foreach (var item in array.EnumerateArray())
        {
            string? due = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("due", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
            items.Add(new BackendActionItem(Str(item, "text"), Str(item, "owner"), due, StrList(item, "segment_ids")));
        }
        return items;
    }

    private static BackendSectionSummary? ParseSectionSummary(BackendDocumentRaw doc)
        => new(doc.SectionId ?? "", Str(doc.Payload, "summary"), StrList(doc.Payload, "bullets"),
            StrList(doc.Payload, "decisions"), StrList(doc.Payload, "action_items"), doc.IsMock);

    private static BackendQaItem? ParseQa(BackendDocumentRaw doc)
    {
        var citations = new List<BackendCitation>();
        if (doc.Payload.ValueKind == JsonValueKind.Object && doc.Payload.TryGetProperty("citations", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var citation in array.EnumerateArray())
                citations.Add(new BackendCitation(Str(citation, "segment_id"), Long(citation, "start_ms") ?? 0,
                    Long(citation, "end_ms") ?? 0, Str(citation, "text")));
        }
        return new BackendQaItem(doc.Id, Str(doc.Payload, "question"), Str(doc.Payload, "answer"), citations,
            (int)(Long(doc.Payload, "dropped_citations") ?? 0), doc.IsMock, doc.Stale, doc.CreatedAt);
    }
}
