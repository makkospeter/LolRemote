using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LolRemote.Companion;

public sealed class PhoneServer
{
    public const int Port = 8765;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".webmanifest"] = "application/manifest+json",
        [".json"] = "application/json"
    };

    readonly EventLog _log;
    readonly Func<ChampSelectController?> _controller;
    readonly ConcurrentDictionary<Guid, PhoneClient> _clients = new();
    volatile bool _dirty = true;
    MdnsResponder? _mdns;
    List<string>? _qrLines;

    public string PairingCode { get; }
    public List<string> Urls { get; private set; } = new();
    public IPAddress? PrimaryAddress { get; private set; }
    public bool Port80 { get; private set; }
    public bool Running { get; private set; }
    public bool MdnsRunning => _mdns?.Running == true;
    public int ClientCount => _clients.Values.Count(c => c.Authenticated && !c.IsDesktop);
    public event Action? ShowRequested;
    public bool OpenBrowserOnFirstRun { get; set; } = true;

    public PhoneServer(EventLog log, Func<ChampSelectController?> controller)
    {
        _log = log;
        _controller = controller;
        PairingCode = LoadOrCreateCode();
        RefreshAddresses();
        _log.Changed += MarkDirty;
    }

    public void MarkDirty() => _dirty = true;

    public string BaseUrl
    {
        get
        {
            var host = PrimaryAddress?.ToString() ?? "localhost";
            return Port80 ? $"http://{host}" : $"http://{host}:{Port}";
        }
    }

    public string ShortUrl => Port80 ? $"http://{MdnsResponder.HostName}" : $"http://{MdnsResponder.HostName}:{Port}";

    public string PairUrl => $"{BaseUrl}/?k={PairingCode}";

    public string LocalSetupUrl => $"http://localhost:{Port}/connect.html";

    public string Describe() =>
        Running
            ? $"{BaseUrl}   Kód: {PairingCode}   Telefon: {(ClientCount > 0 ? $"{ClientCount} csatlakozva" : "nincs")}"
            : "nem fut (lásd az eseményeket)";

    public IReadOnlyList<string> QrLines()
    {
        if (_qrLines is not null) return _qrLines;
        try
        {
            _qrLines = Qr.ConsoleLines(PairUrl);
        }
        catch (Exception)
        {
            _qrLines = new List<string>();
        }
        return _qrLines;
    }

    void RefreshAddresses()
    {
        var addresses = LocalAddresses();
        PrimaryAddress = addresses.FirstOrDefault();
        Urls = addresses.Select(a => $"http://{a}:{Port}").ToList();
        if (Urls.Count == 0) Urls.Add($"http://localhost:{Port}");
    }

    public Task StartAsync(CancellationToken ct) => StartAsync(IsPortFree(80), ct);

    async Task StartAsync(bool tryPort80, CancellationToken ct)
    {
        var webRoot = FindWebRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (tryPort80) options.Listen(IPAddress.Any, 80);
            options.ListenAnyIP(Port);
        });
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", (RequestDelegate)HandleWebSocketAsync);
        app.MapGet("/icon/{id:int}", (int id) => IconAsync(id));
        app.MapGet("/setup-info", (HttpContext context) =>
        {
            if (!IsLocal(context)) return Results.NotFound();
            var controller = _controller();
            return Results.Json(new
            {
                pairUrl = PairUrl,
                baseUrl = BaseUrl,
                shortUrl = MdnsRunning ? ShortUrl : null,
                code = PairingCode,
                phones = ClientCount,
                urls = Urls,
                league = controller is not null,
                phaseText = controller is null ? null : ChampSelectController.PhaseText(controller.Phase),
                mode = controller?.ModeLabel,
                version = typeof(PhoneServer).Assembly.GetName().Version?.ToString(3)
            });
        });
        app.MapGet("/__show", (HttpContext context) =>
        {
            if (!IsLocal(context)) return Results.NotFound();
            ShowRequested?.Invoke();
            return Results.Ok();
        });
        app.MapGet("/setup-qr.png", (HttpContext context) => IsLocal(context)
            ? Results.Bytes(Qr.Png(PairUrl), "image/png")
            : Results.NotFound());
        app.MapFallback("{*path}", (RequestDelegate)(context => ServeStaticAsync(context, webRoot)));

        try
        {
            await app.StartAsync(ct);
        }
        catch (Exception) when (tryPort80)
        {
            try { await app.DisposeAsync(); } catch (Exception) { }
            await StartAsync(false, ct);
            return;
        }
        Port80 = tryPort80;
        _qrLines = null;
        Running = true;
        _log.Add($"Telefonos cím: {BaseUrl}", ConsoleColor.Magenta);

        _mdns = new MdnsResponder(() => PrimaryAddress);
        _mdns.Start(ct);
        if (_mdns.Running) _log.Add($"Rövid cím: {ShortUrl}", ConsoleColor.Magenta);

        _ = Task.Run(() => BroadcastLoopAsync(ct));

        if (OpenBrowserOnFirstRun && !File.Exists(AppPaths.PathFor("phone-paired.txt"))) OpenSetupPage();
    }

    public void OpenSetupPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LocalSetupUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            _log.Add($"Nyisd meg a böngészőben: {LocalSetupUrl}", ConsoleColor.Yellow);
        }
    }

    static bool IsLocal(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static async Task ServeStaticAsync(HttpContext context, string? webRoot)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path == "/" || path == "") path = "/index.html";
        var relative = path.TrimStart('/');
        if (relative.Contains("..") || relative.Contains('\\'))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        byte[]? bytes = null;
        if (webRoot is not null)
        {
            var file = Path.Combine(webRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file);
        }
        bytes ??= EmbeddedFile(relative);
        if (bytes is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = ContentTypes.TryGetValue(Path.GetExtension(relative), out var type) ? type : "application/octet-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.Body.WriteAsync(bytes);
    }

    static byte[]? EmbeddedFile(string relative)
    {
        var assembly = typeof(PhoneServer).Assembly;
        var wanted = "wwwroot/" + relative;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => string.Equals(n.Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    static string? FindWebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
        }
        return null;
    }

    static string LoadOrCreateCode()
    {
        try
        {
            var file = AppPaths.PathFor("pairing-code.txt");
            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (existing.Length == 6 && existing.All(char.IsDigit)) return existing;
            }
            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            File.WriteAllText(file, code);
            return code;
        }
        catch (Exception)
        {
            return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        }
    }

    static readonly string[] VirtualHints = { "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "wsl", "loopback", "bluetooth", "tap-", "tunnel", "zerotier", "tailscale", "hamachi", "radmin" };

    static List<IPAddress> LocalAddresses()
    {
        try
        {
            var candidates = new List<(IPAddress Address, int Score)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var properties = nic.GetIPProperties();
                var text = $"{nic.Name} {nic.Description}".ToLowerInvariant();
                var isVirtual = VirtualHints.Any(text.Contains);
                var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = address.ToString();
                    if (s.StartsWith("169.254.")) continue;
                    var score = (hasGateway ? 0 : 10) + (isVirtual ? 20 : 0)
                        + (s.StartsWith("192.168.") ? 0 : s.StartsWith("10.") ? 1 : 2)
                        + (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1);
                    candidates.Add((address, score));
                }
            }
            return candidates.OrderBy(c => c.Score).Select(c => c.Address).Distinct().ToList();
        }
        catch (Exception)
        {
            return new List<IPAddress>();
        }
    }

    async Task<IResult> IconAsync(int id)
    {
        var c = _controller();
        if (c is null) return Results.NotFound();
        try
        {
            var bytes = await c.ChampionIconAsync(id);
            return bytes is null ? Results.NotFound() : Results.Bytes(bytes, "image/png");
        }
        catch (Exception)
        {
            return Results.NotFound();
        }
    }

    async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var client = new PhoneClient(socket);
        var id = Guid.NewGuid();
        _clients[id] = client;
        try
        {
            await foreach (var text in client.ReceiveAllAsync(context.RequestAborted))
                await HandleMessageAsync(client, text);
        }
        catch (Exception)
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
            if (client.Authenticated && !client.IsDesktop) _log.Add("Telefon lecsatlakozott.", ConsoleColor.DarkGray);
            client.Complete();
            await client.SendLoop;
        }
    }

    async Task HandleMessageAsync(PhoneClient client, string text)
    {
        JsonElement message;
        try
        {
            using var doc = JsonDocument.Parse(text);
            message = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        if (message.ValueKind != JsonValueKind.Object) return;

        var type = Str(message, "type");
        if (type == "auth")
        {
            var ok = Str(message, "code") == PairingCode;
            if (!ok) await Task.Delay(1000);
            client.Authenticated = ok;
            client.IsDesktop = ok && Str(message, "client") == "desktop";
            client.Send(Serialize(new { type = "auth", ok }));
            if (ok && !client.IsDesktop)
            {
                _log.Add("Telefon csatlakozott.", ConsoleColor.Green);
                try { File.WriteAllText(AppPaths.PathFor("phone-paired.txt"), DateTime.Now.ToString("s")); } catch (Exception) { }
            }
            if (ok)
            {
                client.Send(StateJson());
            }
            return;
        }

        if (!client.Authenticated)
        {
            client.Send(Serialize(new { type = "auth", ok = false }));
            return;
        }
        if (type != "cmd") return;

        var requestId = message.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var n) ? n : 0;
        string? error;
        object? data = null;
        try
        {
            (error, data) = await ExecuteAsync(message);
        }
        catch (Exception ex)
        {
            error = $"Hiba: {ex.Message}";
        }
        if (error is not null) _log.Add($"Telefon parancs hiba: {error}", ConsoleColor.Red);
        client.Send(Serialize(new { type = "result", id = requestId, error, data }));
        MarkDirty();
    }

    async Task<(string? Error, object? Data)> ExecuteAsync(JsonElement message)
    {
        var c = _controller();
        if (c is null) return ("Nincs kapcsolat a League klienssel.", null);

        switch (Str(message, "cmd"))
        {
            case "accept":
                return (await c.AcceptAsync(), null);
            case "decline":
                return (await c.DeclineAsync(), null);
            case "hover":
                return (await c.HoverAsync((int)Num(message, "championId")), null);
            case "lock":
                return (await c.LockAsync(), null);
            case "spells":
                return (await c.SetSpellsAsync(Num(message, "spell1"), Num(message, "spell2")), null);
            case "rune":
                return (await c.SetRunePageAsync(Num(message, "pageId")), null);
            case "swap":
                var isTrade = message.TryGetProperty("isTrade", out var t) && t.ValueKind == JsonValueKind.True;
                return (await c.SwapAsync(isTrade, (int)Num(message, "swapId"), Str(message, "verb") ?? ""), null);
            case "setting":
                return (c.SetSetting(Str(message, "name") ?? "", message.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True), null);
            case "template-save":
                return (c.SaveTemplate(), null);
            case "template-apply":
                return (await c.ApplyTemplateAsync(), null);
            case "template-delete":
                return (c.DeleteTemplate(), null);
            case "rune-recommendations":
                var all = message.TryGetProperty("all", out var allEl) && allEl.ValueKind == JsonValueKind.True;
                return await c.RuneRecommendationsAsync(all);
            case "rune-apply-recommended":
                var perkIds = new List<int>();
                if (message.TryGetProperty("perkIds", out var perksEl) && perksEl.ValueKind == JsonValueKind.Array)
                    foreach (var p in perksEl.EnumerateArray())
                        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var perkId)) perkIds.Add(perkId);
                var label = Str(message, "label") ?? "Ajánlott";
                if (label.Length > 40) label = label[..40];
                return (await c.ApplyRecommendedAsync((int)Num(message, "primaryStyleId"), (int)Num(message, "subStyleId"), perkIds, label), null);
            case "counters":
                return await c.CountersAsync((int)Num(message, "championId"));
            case "build":
                return await c.BuildAsync((int)Num(message, "opponentId"));
            case "champions":
                var list = await c.AvailableChampionsAsync();
                return (null, list.Select(x => new { id = x.Id, name = x.Name }).ToList());
            default:
                return ("Ismeretlen parancs.", null);
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    string StateJson()
    {
        var c = _controller();
        var log = _log.Last(15).Select(e => new { time = e.Time, text = e.Text, color = e.Color.ToString() }).ToList();
        return Serialize(new
        {
            type = "state",
            connected = c is not null,
            snapshot = c?.Snapshot(),
            log
        });
    }

    async Task BroadcastLoopAsync(CancellationToken ct)
    {
        var last = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (!_dirty && DateTime.UtcNow - last < TimeSpan.FromSeconds(1)) continue;
            _dirty = false;
            last = DateTime.UtcNow;

            var targets = _clients.Values.Where(c => c.Authenticated).ToList();
            if (targets.Count == 0) continue;
            string json;
            try
            {
                json = StateJson();
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var client in targets) client.Send(json);
        }
    }

    sealed class PhoneClient
    {
        readonly WebSocket _socket;
        readonly Channel<string> _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        public PhoneClient(WebSocket socket)
        {
            _socket = socket;
            SendLoop = Task.Run(SendLoopAsync);
        }

        public bool Authenticated { get; set; }
        public bool IsDesktop { get; set; }
        public Task SendLoop { get; }

        public void Send(string text) => _outbox.Writer.TryWrite(text);

        public void Complete() => _outbox.Writer.TryComplete();

        async Task SendLoopAsync()
        {
            try
            {
                await foreach (var message in _outbox.Reader.ReadAllAsync())
                {
                    if (_socket.State != WebSocketState.Open) break;
                    await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception)
            {
            }
        }

        public async IAsyncEnumerable<string> ReceiveAllAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) yield break;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 64 * 1024) yield break;
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                yield return text;
            }
        }
    }
}
