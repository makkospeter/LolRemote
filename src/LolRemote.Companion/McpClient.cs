using System.Net.Http.Json;
using System.Text.Json;

namespace LolRemote.Companion;

public sealed class McpException : Exception
{
    public McpException(string message) : base(message)
    {
    }
}

public sealed class McpClient : IDisposable
{
    readonly HttpClient _http;
    readonly string _url;
    string? _session;
    string _protocol = "2025-06-18";
    bool _initialized;
    int _nextId;

    public McpClient(string url)
    {
        _url = url;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LolRemote/1.0");
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var result = await RequestAsync("initialize", new
        {
            protocolVersion = _protocol,
            capabilities = new { },
            clientInfo = new { name = "LolRemote", version = "1.0" }
        }, ct);
        if (result.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String)
            _protocol = version.GetString()!;
        _initialized = true;
        await NotifyAsync("notifications/initialized", ct);
    }

    public Task<JsonElement> ListToolsAsync(CancellationToken ct) => RequestAsync("tools/list", new { }, ct);

    public Task<JsonElement> CallToolAsync(string name, object arguments, CancellationToken ct) =>
        RequestAsync("tools/call", new { name, arguments }, ct);

    async Task NotifyAsync(string method, CancellationToken ct)
    {
        using var request = Build(new { jsonrpc = "2.0", method });
        using var response = await _http.SendAsync(request, ct);
    }

    async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        using var request = Build(new { jsonrpc = "2.0", id, method, @params = parameters });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) _session = values.FirstOrDefault() ?? _session;
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new McpException($"HTTP {(int)response.StatusCode}: {Truncate(text)}");

        var json = ExtractJson(text, response.Content.Headers.ContentType?.MediaType, id);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error)) throw new McpException(Truncate(error.ToString()));
        if (!root.TryGetProperty("result", out var result)) throw new McpException($"Váratlan válasz: {Truncate(json)}");
        return result.Clone();
    }

    static string ExtractJson(string text, string? mediaType, int id)
    {
        if (mediaType != "text/event-stream") return text;
        string? last = null;
        foreach (var block in text.Replace("\r\n", "\n").Split("\n\n"))
        {
            var data = string.Join("\n", block.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].TrimStart()));
            if (data.Length == 0) continue;
            last = data;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var n) && n == id) return data;
            }
            catch (JsonException)
            {
            }
        }
        return last ?? "{}";
    }

    HttpRequestMessage Build(object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = JsonContent.Create(body) };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (_session is not null) request.Headers.Add("Mcp-Session-Id", _session);
        if (_initialized) request.Headers.Add("MCP-Protocol-Version", _protocol);
        return request;
    }

    static string Truncate(string text) => text.Length > 600 ? text[..600] + "…" : text;

    public void Dispose() => _http.Dispose();
}

public static class CounterProbe
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var summary = new List<string>();

        var sources = new[]
        {
            ("opgg", "https://mcp-api.op.gg/mcp", new[] { "lol_get_champion_analysis", "lol_get_lane_matchup_guide" }),
            ("metabot", "https://metabot.gg/api/mcp", new[] { "get_counters", "get_entity_build" })
        };

        foreach (var (name, url, toolsToTry) in sources)
        {
            try
            {
                using var client = new McpClient(url);
                await client.InitializeAsync(ct);
                var tools = await client.ListToolsAsync(ct);
                await File.WriteAllTextAsync(Path.Combine(folder, $"{name}-tools.json"), JsonSerializer.Serialize(tools, Pretty), ct);
                var toolList = tools.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().ToList() : new List<JsonElement>();
                summary.Add($"{name}: kapcsolódás OK, {toolList.Count} eszköz");

                foreach (var toolName in toolsToTry)
                {
                    var tool = toolList.FirstOrDefault(x => x.TryGetProperty("name", out var n) && n.GetString() == toolName);
                    if (tool.ValueKind != JsonValueKind.Object)
                    {
                        summary.Add($"  {toolName}: nincs ilyen eszköz");
                        continue;
                    }
                    var schema = tool.TryGetProperty("inputSchema", out var s) ? s : default;
                    var args = GuessArguments(schema, "Aatrox", "top");
                    await File.WriteAllTextAsync(Path.Combine(folder, $"{name}-{toolName}-args.json"), JsonSerializer.Serialize(args, Pretty), ct);
                    try
                    {
                        var result = await client.CallToolAsync(toolName, args, ct);
                        var text = JsonSerializer.Serialize(result, Pretty);
                        await File.WriteAllTextAsync(Path.Combine(folder, $"{name}-{toolName}-result.json"), text, ct);
                        var isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
                        summary.Add($"  {toolName}: {(isError ? "HIBÁS VÁLASZ" : "OK")} ({text.Length} karakter)");
                    }
                    catch (Exception ex)
                    {
                        summary.Add($"  {toolName}: HIBA {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                summary.Add($"{name}: HIBA {ex.GetType().Name}: {ex.Message}");
            }
        }

        await File.WriteAllLinesAsync(Path.Combine(folder, "probe-result.txt"), summary, ct);
        foreach (var line in summary) Console.WriteLine(line);
        return 0;
    }


    public static async Task<int> RunCallsAsync(string folder, string callsFile, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(callsFile, ct));
        using var client = new McpClient("https://mcp-api.op.gg/mcp");
        await client.InitializeAsync(ct);
        var summary = new List<string>();
        foreach (var call in doc.RootElement.EnumerateArray())
        {
            var name = call.GetProperty("name").GetString()!;
            var tool = call.GetProperty("tool").GetString()!;
            try
            {
                var result = await client.CallToolAsync(tool, call.GetProperty("args"), ct);
                var text = JsonSerializer.Serialize(result, Pretty);
                await File.WriteAllTextAsync(Path.Combine(folder, $"call-{name}.json"), text, ct);
                summary.Add($"{name}: OK ({text.Length} karakter)");
            }
            catch (Exception ex)
            {
                summary.Add($"{name}: HIBA {ex.Message}");
            }
        }
        await File.WriteAllLinesAsync(Path.Combine(folder, "calls-result.txt"), summary, ct);
        foreach (var line in summary) Console.WriteLine(line);
        return 0;
    }

    static Dictionary<string, object?> GuessArguments(JsonElement schema, string champion, string position)
    {
        var args = new Dictionary<string, object?>();
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties)) return args;
        foreach (var property in properties.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            string? wanted =
                name.Contains("mode") ? "ranked"
                : name.Contains("game") ? "league"
                : name.Contains("pos") || name.Contains("role") || name.Contains("lane") ? position
                : name.Contains("lang") || name.Contains("locale") ? "en_US"
                : name.Contains("region") ? "euw"
                : name.Contains("champ") || name.Contains("entity") || name.Contains("hero") || name.Contains("name") || name.Contains("query") || name.Contains("slug") ? champion
                : null;

            object? value = null;
            if (property.Value.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                var values = options.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString()!).ToList();
                value = wanted is null ? null : values.FirstOrDefault(v => Normalize(v) == Normalize(wanted))
                    ?? values.FirstOrDefault(v => Normalize(v).Contains(Normalize(wanted)));
            }
            else if (name.Contains("desired_output") || name.Contains("fields"))
            {
                var type = property.Value.TryGetProperty("type", out var ty) ? ty.ToString() : "";
                value = type.Contains("array") ? new[] { "*" } : "*";
            }
            else if (wanted is not null)
            {
                value = name.Contains("champ") && property.Value.TryGetProperty("description", out var d) && d.ToString().Contains("UPPER", StringComparison.OrdinalIgnoreCase)
                    ? wanted.ToUpperInvariant()
                    : wanted;
            }

            if (value is not null) args[property.Name] = value;
        }
        return args;
    }

    static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
