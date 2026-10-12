using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace MeetVault.Core.Backend;

// One HTTP request against the backend; Path is server-relative ("/v1/meetings").
// Query values travel as query-string parameters; UploadPath makes the request multipart
// with the file under the field name the backend expects ("file").
public sealed record BackendRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string>? Query = null,
    string? JsonBody = null,
    string? UploadPath = null,
    string? UploadMime = null);

// Any non-2xx response, mapped from the backend's uniform error envelope:
// {"error": {"code","message","detail"}, "request_id"}
public sealed class BackendApiException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }
    public string? RequestId { get; }

    public BackendApiException(int statusCode, string code, string message, string? requestId)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        RequestId = requestId;
    }
}

public interface IBackendTransport : IDisposable
{
    // Response body text for any 2xx; throws BackendApiException otherwise.
    Task<string> SendAsync(BackendRequest request, CancellationToken ct);
    // GET with Range resume into destination + ".part", moved into place on completion.
    Task DownloadToFileAsync(string path, string destinationPath, CancellationToken ct);
    // Raw lines of a text/event-stream response, yielded as they arrive.
    IAsyncEnumerable<string> StreamLinesAsync(string path, CancellationToken ct);
}

public sealed class BackendHttpTransport : IBackendTransport
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public BackendHttpTransport(string baseUrl, string apiKey, HttpMessageHandler? handler = null)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Backend URL must be an absolute http(s) URL.", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Backend API key is required.", nameof(apiKey));
        _baseUri = uri;
        _http = new HttpClient(handler ?? new HttpClientHandler());
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> SendAsync(BackendRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), BuildUri(request.Path, request.Query));
        if (request.JsonBody is not null)
            message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");
        if (request.UploadPath is { } path)
        {
            var form = new MultipartFormDataContent();
            var content = new StreamContent(File.OpenRead(path));
            content.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrEmpty(request.UploadMime) ? "application/octet-stream" : request.UploadMime);
            form.Add(content, "file", Path.GetFileName(path));
            message.Content = form;
        }
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return body;
        throw ToApiException(response.StatusCode, body);
    }

    public async Task DownloadToFileAsync(string path, string destinationPath, CancellationToken ct)
    {
        var full = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var part = full + ".part";
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var message = new HttpRequestMessage(HttpMethod.Get, BuildUri(path, null));
        if (existing > 0) message.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Nothing beyond what we already have: the part file IS the complete download.
            File.Move(part, full, overwrite: true);
            return;
        }
        if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
            existing = 0; // the server ignored Range: start over
        if (!response.IsSuccessStatusCode)
            throw ToApiException(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        await using (var file = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await response.Content.CopyToAsync(file, ct);
        }
        File.Move(part, full, overwrite: true);
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, BuildUri(path, null));
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw ToApiException(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            yield return line;
        }
    }

    private Uri BuildUri(string path, IReadOnlyDictionary<string, string>? query)
    {
        var builder = new UriBuilder(_baseUri)
        {
            Path = _baseUri.AbsolutePath.TrimEnd('/') + "/" + path.TrimStart('/'),
        };
        if (query is { Count: > 0 })
        {
            builder.Query = string.Join("&", query
                .Where(kv => kv.Value.Length > 0)
                .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        }
        return builder.Uri;
    }

    private static BackendApiException ToApiException(HttpStatusCode status, string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var error = doc.RootElement.GetProperty("error");
            var code = error.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var requestId = doc.RootElement.TryGetProperty("request_id", out var r) ? r.GetString() : null;
            if (code.Length > 0) return new BackendApiException((int)status, code, message, requestId);
        }
        catch (System.Text.Json.JsonException) { }
        return new BackendApiException((int)status, "http_error", "The backend answered with status " + (int)status + ".", null);
    }

    public void Dispose() => _http.Dispose();
}
