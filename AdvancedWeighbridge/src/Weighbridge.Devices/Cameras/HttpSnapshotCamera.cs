using System.Net;
using System.Net.Http.Headers;

namespace Weighbridge.Devices.Cameras;

public enum CameraAuth { None, Basic, Digest }

public sealed record CameraConfig
{
    public string Id { get; init; } = "cam1";
    public string Name { get; init; } = "Front camera";

    /// <summary>JPEG snapshot URL, e.g. Hikvision <c>http://ip/ISAPI/Streaming/channels/101/picture</c>,
    /// Dahua <c>http://ip/cgi-bin/snapshot.cgi</c>, Axis <c>http://ip/axis-cgi/jpg/image.cgi</c>.</summary>
    public string SnapshotUrl { get; init; } = "http://192.168.1.64/ISAPI/Streaming/channels/101/picture";

    public string? Username { get; init; }

    /// <summary>Stored encrypted by the application (DPAPI); plain text only in memory.</summary>
    public string? Password { get; init; }

    public CameraAuth Auth { get; init; } = CameraAuth.Digest;
    public int TimeoutMs { get; init; } = 3000;
    public bool CaptureOnWeighing { get; init; } = true;
    public bool ShowPreview { get; init; } = true;
    public int PreviewIntervalMs { get; init; } = 1000;
}

public sealed record CameraSnapshot(string CameraId, string CameraName, byte[] Data, string ContentType, DateTimeOffset CapturedAt);

public interface ICamera : IDisposable
{
    CameraConfig Config { get; }
    Task<CameraSnapshot> CaptureAsync(CancellationToken ct);
}

/// <summary>Captures still images from IP cameras over HTTP. Only real JPEG/PNG data is accepted.</summary>
public sealed class HttpSnapshotCamera : ICamera
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeProvider _time;

    public HttpSnapshotCamera(CameraConfig config, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        Config = config;
        _time = time ?? TimeProvider.System;
        if (handler is null)
        {
            var h = new SocketsHttpHandler { PreAuthenticate = config.Auth == CameraAuth.Basic, ConnectTimeout = TimeSpan.FromMilliseconds(config.TimeoutMs) };
            if (config.Auth != CameraAuth.None && config.Username is not null)
            {
                var uri = new Uri(config.SnapshotUrl);
                var cache = new CredentialCache
                {
                    { new Uri(uri.GetLeftPart(UriPartial.Authority)), config.Auth == CameraAuth.Digest ? "Digest" : "Basic", new NetworkCredential(config.Username, config.Password ?? string.Empty) },
                };
                h.Credentials = cache;
            }
            handler = h;
        }
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMilliseconds(Math.Max(500, config.TimeoutMs)) };
        _ownsClient = true;
        if (config.Auth == CameraAuth.Basic && config.Username is not null)
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public CameraConfig Config { get; }

    public async Task<CameraSnapshot> CaptureAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(Config.SnapshotUrl, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new IOException($"{Config.Name}: authentication failed.");
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var type = DetectImageType(data) ?? throw new IOException($"{Config.Name}: the camera did not return a JPEG or PNG image.");
        if (data.Length > 20 * 1024 * 1024) throw new IOException($"{Config.Name}: image too large.");
        return new CameraSnapshot(Config.Id, Config.Name, data, type, _time.GetUtcNow());
    }

    public static string? DetectImageType(ReadOnlySpan<byte> data)
    {
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length > 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        return null;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
