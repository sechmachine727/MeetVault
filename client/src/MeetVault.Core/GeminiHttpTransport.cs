using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MeetVault.Core;

// Real Gemini transport over HttpClient: Files API upload + status poll + interactions call.
// ponytail: no GenAI SDK (it lacks an Interactions client); key travels per-request, never stored here.
public sealed class GeminiHttpTransport : IGeminiTransport, IDisposable
{
    private const string Base = "https://generativelanguage.googleapis.com";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiHttpTransport(string apiKey, string model = "gemini-3.8-flash", HttpClient? http = null)
    {
        _apiKey = apiKey;
        _model = model;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<string> UploadFileAsync(string path, Stream content, string mime, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(streamContent, "file", Path.GetFileName(path));
        using var res = await _http.PostAsync($"{Base}/upload/v1beta/files?key={_apiKey}", form, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var name = doc.RootElement.GetProperty("file").GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Upload response missing file.name.");
        for (var i = 0; i < 120; i++) // poll ~5 s while PROCESSING
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var get = await _http.GetAsync($"{Base}/v1beta/{name}?key={_apiKey}", ct);
            get.EnsureSuccessStatusCode();
            using var state = JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct));
            var st = state.RootElement.GetProperty("state").GetString();
            if (st == "ACTIVE") return name;
            if (st == "FAILED") throw new InvalidOperationException("Gemini file processing FAILED.");
        }
        throw new TimeoutException("Gemini file stayed PROCESSING too long.");
    }

    public async Task<string> InteractAsync(string fileId, string transcriptJson, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            input = new object[]
            {
                new { file = fileId },
                new { text = "Refine this meeting transcript, then give analysis and a summary. Supporting timestamped transcript JSON follows.\n" + transcriptJson },
            },
        });
        using var res = await _http.PostAsync($"{Base}/v1beta/interactions?key={_apiKey}", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    public void Dispose() => _http.Dispose();
}

