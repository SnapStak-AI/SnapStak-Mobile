using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SnapStakMobile.Services;

/// <summary>
/// CON10X Enterprise pipeline service.
///
/// Replaces MobileConteXPipelineService for the Enterprise build.
/// Instead of running the CON10X engine on-device, it:
///
///   Stage 1 — POST extracted DOM JSON to the Enterprise server
///              POST /web-to-structure/transform
///              Server runs StructureAgent, writes SVG + pillar files server-side.
///
///   Stage 2 — TransformPage fires POST /structure-to-code/process-session
///              after the user selects a framework.
///              Server runs Behaviour AI + Constructor.
///
///   Stage 3 — SSE stream delivers progress events: COMPONENT_COMPLETE,
///              SESSION_COMPLETE (with downloadToken), COMPONENT_ERROR.
///
///   Stage 4 — GET /structure-to-code/download/{token}
///              Downloads the generated code package zip.
///
/// The enterprise server URL is configured in SettingsPage and stored in
/// SecureStorage as "enterpriseServerUrl".
/// The enterprise API key is stored in SecureStorage as "snapstakApiKey".
/// </summary>
public sealed class EnterpriseConteXService
{
    private static readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    // ── Stage 1: POST DOM snapshot to Enterprise server ───────────────────────

    /// <summary>
    /// Sends the extracted DOM JSON to the Enterprise server.
    /// The server runs the full StructureAgent pipeline and stores all pillar
    /// files server-side. No local processing happens.
    /// </summary>
    public async Task<EnterpriseTransformResult> TransformAsync(
        string extractedJson,
        string userUuid,
        string framework,
        IProgress<string>? progress = null)
    {
        progress?.Report("Connecting to Enterprise server...");

        var apiKey = await SecureStorage.Default.GetAsync("snapstakApiKey");
        if (string.IsNullOrEmpty(apiKey))
            throw new UnauthorizedAccessException("No enterprise API key configured. Please add your key in Settings.");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{GetServerUrl()}/web-to-structure/transform")
        {
            Content = new StringContent(extractedJson, Encoding.UTF8, "application/json"),
        };

        AddEnterpriseHeaders(request, apiKey, userUuid);
        request.Headers.Add("X-ConteX-Action", "web-to-structure/transform");
        request.Headers.Add("X-SnapStak-Framework", framework);

        progress?.Report("Uploading to Enterprise server...");

        var response = await _client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException($"Enterprise server rejected API key (401): {body}");
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException($"Access denied (403): {body}");
            throw new Exception($"Enterprise server returned {(int)response.StatusCode}: {body}");
        }

        progress?.Report("Structure pillar complete — ready to generate.");

        return new EnterpriseTransformResult { Success = true };
    }

    // ── Stage 2: Fire process-session ─────────────────────────────────────────

    /// <summary>
    /// POSTs to /structure-to-code/process-session.
    /// The server starts the Behaviour AI + Constructor pipeline in the background.
    /// Progress arrives via SSE (ListenToSessionSseAsync).
    /// Returns the total component count so the UI can show a progress bar.
    /// </summary>
    public async Task<int> StartProcessSessionAsync(
        string userUuid,
        string frameworkKey,
        string platformType)
    {
        var apiKey = await SecureStorage.Default.GetAsync("snapstakApiKey");
        if (string.IsNullOrEmpty(apiKey))
            throw new UnauthorizedAccessException("No enterprise API key configured.");

        var payload = JsonSerializer.Serialize(new
        {
            framework    = frameworkKey,
            platformType = platformType,
            styleOutput  = "css",
            language     = "js",
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{GetServerUrl()}/structure-to-code/process-session")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        AddEnterpriseHeaders(request, apiKey, userUuid);
        request.Headers.Add("X-ConteX-Action", "structure-to-code/process-session");

        var response = await _client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException($"Unauthorised (401): {body}");
            throw new Exception($"Server returned {(int)response.StatusCode}: {body}");
        }

        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return result.RootElement.TryGetProperty("componentCount", out var cc)
            ? cc.GetInt32() : 0;
    }

    // ── Stage 3: SSE listener ─────────────────────────────────────────────────

    /// <summary>
    /// Connects to the SSE endpoint and streams progress events until
    /// SESSION_COMPLETE or cancellation.
    ///
    /// Calls onEvent for each parsed SSE event:
    ///   { type: "COMPONENT_COMPLETE", index, total, label }
    ///   { type: "SESSION_COMPLETE", componentCount, framework, downloadToken }
    ///   { type: "COMPONENT_ERROR", index, label, error }
    /// </summary>
    public async Task ListenToSessionSseAsync(
        string userUuid,
        Action<EnterpriseSessionEvent> onEvent,
        CancellationToken ct)
    {
        var apiKey = await SecureStorage.Default.GetAsync("snapstakApiKey");
        if (string.IsNullOrEmpty(apiKey)) return;

        var sseClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"{GetServerUrl()}/structure-to-code/events/session/{userUuid}");

        AddEnterpriseHeaders(request, apiKey, userUuid);
        request.Headers.Add("Accept", "text/event-stream");

        try
        {
            using var response = await sseClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode) return;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);

            while (!ct.IsCancellationRequested && !reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var json = line["data:".Length..].Trim();
                if (string.IsNullOrEmpty(json)) continue;

                try
                {
                    using var doc  = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                    var evt = new EnterpriseSessionEvent
                    {
                        Type          = type ?? "",
                        Index         = root.TryGetProperty("index",          out var i)  ? i.GetInt32()     : 0,
                        Total         = root.TryGetProperty("total",          out var tt) ? tt.GetInt32()    : 0,
                        Label         = root.TryGetProperty("label",          out var l)  ? l.GetString()    : null,
                        Error         = root.TryGetProperty("error",          out var e)  ? e.GetString()    : null,
                        ComponentCount= root.TryGetProperty("componentCount", out var cc) ? cc.GetInt32()    : 0,
                        Framework     = root.TryGetProperty("framework",      out var fw) ? fw.GetString()   : null,
                        DownloadToken = root.TryGetProperty("downloadToken",  out var d)  ? d.GetString()    : null,
                    };

                    onEvent(evt);

                    if (type == "SESSION_COMPLETE" || type == "SESSION_ERROR") return;
                }
                catch (JsonException) { /* malformed event — skip */ }
            }
        }
        catch (OperationCanceledException) { /* normal cancellation */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnapStak Enterprise] SSE error: {ex.Message}");
        }
    }

    // ── Stage 4: Download generated package ───────────────────────────────────

    /// <summary>
    /// Downloads the generated code zip from the Enterprise server.
    /// Returns the bytes of the zip file for saving or sharing.
    /// </summary>
    public async Task<byte[]> DownloadPackageAsync(string downloadToken, string userUuid)
    {
        var apiKey = await SecureStorage.Default.GetAsync("snapstakApiKey");
        if (string.IsNullOrEmpty(apiKey))
            throw new UnauthorizedAccessException("No enterprise API key configured.");

        var request = new HttpRequestMessage(
            HttpMethod.Get, $"{GetServerUrl()}/structure-to-code/download/{downloadToken}");

        AddEnterpriseHeaders(request, apiKey, userUuid);

        var response = await _client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Exception($"Download failed ({(int)response.StatusCode}): {body}");
        }

        return await response.Content.ReadAsByteArrayAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────


    private static string GetServerUrl()
    {
        try
        {
            var stored = SecureStorage.Default.GetAsync("enterpriseServerUrl")
                .GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(stored)
                ? "https://enterprise.snapstak.ai"
                : stored.TrimEnd('/');
        }
        catch { return "https://enterprise.snapstak.ai"; }
    }

    private static void AddEnterpriseHeaders(
        HttpRequestMessage request,
        string apiKey,
        string userUuid)
    {
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("X-User-UUID",      userUuid);
        request.Headers.Add("X-ConteX-Domain",  "web");
        request.Headers.Add("X-ConteX-Client",  "maui-enterprise");
    }
}

// ── Result / event types ──────────────────────────────────────────────────────

public sealed class EnterpriseTransformResult
{
    public bool    Success { get; set; }
    public string? Error   { get; set; }
}

public sealed class EnterpriseSessionEvent
{
    public string  Type           { get; set; } = string.Empty;
    public int     Index          { get; set; }
    public int     Total          { get; set; }
    public string? Label          { get; set; }
    public string? Error          { get; set; }
    public int     ComponentCount { get; set; }
    public string? Framework      { get; set; }
    public string? DownloadToken  { get; set; }
}
