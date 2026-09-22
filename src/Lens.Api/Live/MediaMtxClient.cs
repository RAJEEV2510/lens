using System.Net.Http.Json;
using System.Text.Json;

namespace Lens.Api.Live;

/// <summary>
/// Talks to a MediaMTX server so each Lens camera gets a WebRTC playback path. MediaMTX pulls the camera's RTSP itself
/// (on demand, only while someone watches) and hands the browser a WHEP endpoint. If MediaMTX is not running, everything
/// still works: the UI falls back to Lens's own MJPEG stream.
/// </summary>
public sealed class MediaMtxClient
{
    private readonly HttpClient _http;
    private readonly string? _apiUrl;
    private readonly string? _webRtcUrl;
    private readonly ILogger<MediaMtxClient> _log;
    private bool? _available;
    private DateTimeOffset _checkedAt;

    public MediaMtxClient(IConfiguration config, ILogger<MediaMtxClient> log)
    {
        _log = log;
        _apiUrl = (config["MEDIAMTX_API"] ?? config["Lens:MediaMtx:ApiUrl"] ?? "http://localhost:9997").TrimEnd('/');
        _webRtcUrl = (config["MEDIAMTX_WEBRTC"] ?? config["Lens:MediaMtx:WebRtcUrl"] ?? "http://localhost:8889").TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>Base URL browsers use for WHEP, e.g. http://host:8889. Path name is appended by the client.</summary>
    public string WebRtcBaseUrl => _webRtcUrl ?? "";

    public static string PathName(int sourceId) => $"lens-{sourceId}";

    /// <summary>Cached for 10 seconds so the status endpoint does not hammer the server.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (_available is { } a && DateTimeOffset.UtcNow - _checkedAt < TimeSpan.FromSeconds(10)) return a;
        try
        {
            using var r = await _http.GetAsync($"{_apiUrl}/v3/config/global/get", ct);
            _available = r.IsSuccessStatusCode;
        }
        catch (Exception) { _available = false; }
        _checkedAt = DateTimeOffset.UtcNow;
        return _available.Value;
    }

    /// <summary>Creates or updates the playback path for a source. Returns false when MediaMTX is unreachable.</summary>
    public async Task<bool> EnsurePathAsync(int sourceId, string rtspUrl, CancellationToken ct)
    {
        if (!await IsAvailableAsync(ct)) return false;
        var name = PathName(sourceId);
        // TCP transport for the pull: UDP drops RTP packets on busy hosts and the picture breaks up.
        var body = new { source = rtspUrl, sourceOnDemand = true, sourceOnDemandStartTimeout = "10s", sourceOnDemandCloseAfter = "10s", rtspTransport = "tcp" };
        try
        {
            using var add = await _http.PostAsJsonAsync($"{_apiUrl}/v3/config/paths/add/{name}", body, ct);
            if (add.IsSuccessStatusCode) return true;
            // Already exists: patch it so a changed URL takes effect.
            using var patch = await _http.PatchAsync($"{_apiUrl}/v3/config/paths/patch/{name}", JsonContent.Create(body), ct);
            if (!patch.IsSuccessStatusCode)
                _log.LogWarning("mediamtx: could not register path {Name}: {Status}", name, patch.StatusCode);
            return patch.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning("mediamtx: {Error}", ex.Message);
            return false;
        }
    }

    public async Task RemovePathAsync(int sourceId, CancellationToken ct)
    {
        if (!await IsAvailableAsync(ct)) return;
        try
        {
            using var r = await _http.DeleteAsync($"{_apiUrl}/v3/config/paths/delete/{PathName(sourceId)}", ct);
        }
        catch (Exception ex) { _log.LogDebug("mediamtx: {Error}", ex.Message); }
    }

    /// <summary>Whether MediaMTX currently has the stream ready for playback.</summary>
    public async Task<bool> IsPathReadyAsync(int sourceId, CancellationToken ct)
    {
        if (!await IsAvailableAsync(ct)) return false;
        try
        {
            using var r = await _http.GetAsync($"{_apiUrl}/v3/paths/get/{PathName(sourceId)}", ct);
            if (!r.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("ready", out var ready) && ready.GetBoolean();
        }
        catch (Exception) { return false; }
    }
}
