using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LolRemote.Companion;

public record LockfileInfo(int Port, string Password, string Protocol)
{
    static readonly string[] KnownPaths =
    {
        @"C:\Riot Games\League of Legends\lockfile",
        @"D:\Riot Games\League of Legends\lockfile",
        @"E:\Riot Games\League of Legends\lockfile",
        @"C:\Program Files\Riot Games\League of Legends\lockfile",
        @"D:\Games\Riot Games\League of Legends\lockfile"
    };

    public static string? DetectPath()
    {
        foreach (var name in new[] { "LeagueClientUx", "LeagueClient" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var process in processes)
            {
                try
                {
                    var exe = process.MainModule?.FileName;
                    var dir = exe is null ? null : Path.GetDirectoryName(exe);
                    for (var i = 0; i < 3 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
                    {
                        var candidate = Path.Combine(dir, "lockfile");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        return KnownPaths.FirstOrDefault(File.Exists);
    }

    public static LockfileInfo? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var parts = reader.ReadToEnd().Trim().Split(':');
            if (parts.Length < 5) return null;
            return new LockfileInfo(int.Parse(parts[2]), parts[3], parts[4]);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class LcuConnection : IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    readonly LockfileInfo _info;
    readonly HttpClient _http;
    readonly ClientWebSocket _socket = new();
    readonly string _authHeader;

    public event Action<string, string, JsonElement>? EventReceived;

    public LcuConnection(LockfileInfo info)
    {
        _info = info;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{info.Password}"));
        _authHeader = $"Basic {token}";
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{info.Protocol}://127.0.0.1:{info.Port}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _socket.Options.AddSubProtocol("wamp");
        _socket.Options.SetRequestHeader("Authorization", _authHeader);
        _socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var scheme = _info.Protocol == "https" ? "wss" : "ws";
        await _socket.ConnectAsync(new Uri($"{scheme}://127.0.0.1:{_info.Port}/"), ct);
        await _socket.SendAsync(Encoding.UTF8.GetBytes("[5, \"OnJsonApiEvent\"]"), WebSocketMessageType.Text, true, ct);
    }

    public async Task ListenAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            HandleMessage(text);
        }
    }

    void HandleMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3) return;
            if (root[0].ValueKind != JsonValueKind.Number || root[0].GetInt32() != 8) return;
            var payload = root[2];
            var uri = payload.GetProperty("uri").GetString() ?? "";
            var eventType = payload.GetProperty("eventType").GetString() ?? "";
            var data = payload.TryGetProperty("data", out var d) ? d.Clone() : default;
            EventReceived?.Invoke(uri, eventType, data);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
    }

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    public async Task<byte[]?> GetBytesAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, CancellationToken ct = default) =>
        _http.PostAsync(path, ToContent(body), ct);

    public Task<HttpResponseMessage> PatchAsync(string path, object body, CancellationToken ct = default) =>
        _http.PatchAsync(path, ToContent(body), ct);

    public Task<HttpResponseMessage> PutAsync(string path, object body, CancellationToken ct = default) =>
        _http.PutAsync(path, ToContent(body), ct);

    static HttpContent? ToContent(object? body) =>
        body is null ? null : JsonContent.Create(body, options: JsonOptions);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch (Exception)
        {
        }
        _socket.Dispose();
        _http.Dispose();
    }
}
