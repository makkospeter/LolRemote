using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LolRemote.Companion;

public sealed class SelfTest
{
    readonly PhoneServer _phone;
    readonly Func<ChampSelectController?> _controller;
    readonly List<string> _lines = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _results = new();
    readonly object _stateLock = new();
    ClientWebSocket? _socket;
    TaskCompletionSource<bool>? _auth;
    JsonElement? _snapshot;
    int _nextId = 1;

    public SelfTest(PhoneServer phone, Func<ChampSelectController?> controller)
    {
        _phone = phone;
        _controller = controller;
    }

    void Report(bool ok, string name)
    {
        var line = $"{(ok ? "PASS" : "FAIL")}  {name}";
        _lines.Add(line);
        Console.WriteLine(line);
    }

    void Info(string text)
    {
        var line = $"      {text}";
        _lines.Add(line);
        Console.WriteLine(line);
    }

    public async Task<int> RunAsync(string logPath, CancellationToken ct)
    {
        try
        {
            await RunStepsAsync(ct);
        }
        catch (Exception ex)
        {
            Report(false, $"Váratlan kivétel: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            if (_controller() is { } c) await c.MockExitAsync();
        }
        catch (Exception)
        {
        }

        var failed = _lines.Count(l => l.StartsWith("FAIL"));
        var summary = failed == 0 ? "EREDMÉNY: MINDEN TESZT SIKERES" : $"EREDMÉNY: {failed} HIBA";
        _lines.Add(summary);
        Console.WriteLine(summary);
        File.WriteAllLines(logPath, _lines, Encoding.UTF8);
        return failed == 0 ? 0 : 1;
    }

    async Task RunStepsAsync(CancellationToken ct)
    {
        var controller = await WaitForAsync(() => _controller(), TimeSpan.FromSeconds(30), ct);
        Report(controller is not null, "Társprogram kapcsolódott a mock klienshez");
        if (controller is null) return;

        Report(_phone.Running, $"Telefonos szerver fut ({PhoneServer.Port}-es port)");
        if (!_phone.Running) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var index = await http.GetStringAsync($"http://127.0.0.1:{PhoneServer.Port}/", ct);
        Report(index.Contains("LolRemote"), "Weboldal (index.html) kiszolgálása");
        var appJs = await http.GetAsync($"http://127.0.0.1:{PhoneServer.Port}/app.js", ct);
        Report(appJs.IsSuccessStatusCode, "app.js kiszolgálása");

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{PhoneServer.Port}/ws"), ct);
        _ = Task.Run(() => ReceiveLoopAsync(ct), ct);

        Report(await AuthAsync("000000", ct) == false, "Hibás párosító kód elutasítva");
        Report(await AuthAsync(_phone.PairingCode, ct), "Helyes párosító kóddal csatlakozik");

        var state = await WaitStateAsync(s => true, 5, ct);
        Report(state is not null, "Állapot-üzenet érkezik a telefonra");

        var runeOutside = await CommandAsync(new { cmd = "rune", pageId = 102 }, ct);
        Report(Error(runeOutside) is not null, $"Rúnaválasztás champ selecten kívül tiltva ({Error(runeOutside)})");

        var unknown = await CommandAsync(new { cmd = "nincs-ilyen" }, ct);
        Report(Error(unknown) is not null, "Ismeretlen parancs hibát ad");

        Report(await controller.MockStartQueueAsync() is null, "Mock meccskeresés indítása");
        if (controller.ModeLabel == "Practice Tool")
        {
            await RunPracticeAsync(ct);
            return;
        }
        state = await WaitStateAsync(s => Phase(s) == "ReadyCheck", 15, ct);
        Report(state is not null, "Ready check megjelenik a telefonon");
        if (state is null) return;

        var accept = await CommandAsync(new { cmd = "accept" }, ct);
        Report(Error(accept) is null, "Elfogadás telefonról");
        state = await WaitStateAsync(s => Phase(s) == "ChampSelect" && s.TryGetProperty("champSelect", out var cs) && cs.ValueKind == JsonValueKind.Object, 15, ct);
        Report(state is not null, "Champ select elindul");
        if (state is null) return;

        var (banned, picked, pickedId) = await PlayChampSelectAsync(null, ct);
        Report(banned, "Ban kör végigment");
        Report(picked, "Pick kör végigment");

        state = await WaitStateAsync(s => TimerPhase(s) == "FINALIZATION", 60, ct);
        Report(state is not null, "Finalizáció elérve");
        if (state is null) return;

        var spells = await CommandAsync(new { cmd = "spells", spell1 = 4, spell2 = 12 }, ct);
        Report(Error(spells) is null, "Spell csere (Flash + Teleport)");
        Report(await WaitStateAsync(s => Spell(s, "spell2") == 12, 5, ct) is not null, "Spell csere megjelenik az állapotban");

        var sameSpell = await CommandAsync(new { cmd = "spells", spell1 = 4, spell2 = 4 }, ct);
        Report(Error(sameSpell) is not null, "Két azonos spell tiltva");

        var rune = await CommandAsync(new { cmd = "rune", pageId = 102 }, ct);
        Report(Error(rune) is null, "Rúnaoldal választás champ selectben");
        Report(await WaitStateAsync(s => CurrentRune(s) == 102, 5, ct) is not null, "Rúnaoldal csere megjelenik az állapotban");

        var badSwap = await CommandAsync(new { cmd = "swap", isTrade = true, swapId = 1, verb = "hack" }, ct);
        Report(Error(badSwap) is not null, "Érvénytelen csere-művelet tiltva");

        var save = await CommandAsync(new { cmd = "template-save" }, ct);
        Report(Error(save) is null, $"Sablon mentése {Error(save)}");
        Report(await WaitStateAsync(s => TemplateExists(s) == true, 5, ct) is not null, "A sablon megjelenik az állapotban");

        await CommandAsync(new { cmd = "rune", pageId = 101 }, ct);
        await WaitStateAsync(s => CurrentRune(s) == 101, 5, ct);
        var apply = await CommandAsync(new { cmd = "template-apply" }, ct);
        Report(Error(apply) is null, $"Sablon alkalmazása (új LolRemote oldal) {Error(apply)}");
        Report(await WaitStateAsync(s => CurrentRuneName(s)?.StartsWith("LolRemote") == true, 5, ct) is not null, "A LolRemote rúnaoldal lett az aktív");
        var pagesAfterFirst = RunePageCount(Snapshot());

        await CommandAsync(new { cmd = "rune", pageId = 101 }, ct);
        await WaitStateAsync(s => CurrentRune(s) == 101, 5, ct);
        var applyAgain = await CommandAsync(new { cmd = "template-apply" }, ct);
        Report(Error(applyAgain) is null, $"Sablon újra alkalmazása (meglévő oldal felülírása) {Error(applyAgain)}");
        Report(await WaitStateAsync(s => CurrentRuneName(s)?.StartsWith("LolRemote") == true, 5, ct) is not null, "Újra a LolRemote oldal az aktív");
        Report(pagesAfterFirst is not null && RunePageCount(Snapshot()) == pagesAfterFirst, $"Nem jön létre felesleges rúnaoldal ({RunePageCount(Snapshot())} db)");

        await CheckRecommendationsAsync(ct);

        var autoOn = await CommandAsync(new { cmd = "setting", name = "autoAccept", value = true }, ct);
        Report(Error(autoOn) is null && await WaitStateAsync(s => Setting(s, "autoAccept") == true, 5, ct) is not null, "Auto-accept bekapcsolása");

        var ended = await WaitStateAsync(s => Phase(s) == "InProgress", 40, ct);
        Report(ended is not null, "Champ select vége, játék indul");
        if (ended is null) return;

        Info("--- Második kör: auto-accept és automatikus sablon ---");
        Report(await controller.MockStartQueueAsync() is null, "Új meccskeresés");
        state = await WaitStateAsync(s => Phase(s) == "ReadyCheck", 15, ct);
        Report(state is not null, "Ready check megjelenik");
        var autoAccepted = await WaitStateAsync(s => Phase(s) == "ChampSelect" || ReadyResponse(s) == "Accepted", 10, ct);
        Report(autoAccepted is not null, "Auto-accept magától elfogadta a meccset");
        state = await WaitStateAsync(s => Phase(s) == "ChampSelect" && TimerPhase(s) is not null, 15, ct);
        Report(state is not null, "Champ select elindul (második kör)");
        if (state is null) return;

        await CommandAsync(new { cmd = "rune", pageId = 101 }, ct);
        await WaitStateAsync(s => CurrentRune(s) == 101, 5, ct);
        var second = await PlayChampSelectAsync(pickedId, ct);
        if (second.PickedId == pickedId)
        {
            Report(await WaitStateAsync(s => CurrentRuneName(s)?.StartsWith("LolRemote") == true, 8, ct) is not null,
                "Lock után a sablon magától alkalmazódott");
        }
        else
        {
            Info("A sablonos champion a második körben nem volt elérhető, az automatikus sablon tesztje kimarad.");
        }

        var autoOff = await CommandAsync(new { cmd = "setting", name = "autoAccept", value = false }, ct);
        Report(Error(autoOff) is null && await WaitStateAsync(s => Setting(s, "autoAccept") == false, 5, ct) is not null, "Auto-accept kikapcsolása");

        var ended2 = await WaitStateAsync(s => Phase(s) == "InProgress", 60, ct);
        Report(ended2 is not null, "Második champ select vége");
    }

    async Task RunPracticeAsync(CancellationToken ct)
    {
        Info("--- Practice Tool mód ---");
        var state = await WaitStateAsync(s => Phase(s) == "ChampSelect" && TimerPhase(s) is not null, 10, ct);
        Report(state is not null, "Champ select ready check nélkül indul");
        if (state is null) return;
        Report(Mode(state.Value) == "Practice Tool", $"Játékmód felismerve ({Mode(state.Value)})");
        var cs = state.Value.GetProperty("champSelect");
        Report(cs.GetProperty("theirTeam").GetArrayLength() == 0, "Nincs ellenfél, a felület ezt kezeli");
        Report(cs.GetProperty("myTeam").GetArrayLength() == 1, "Egyedül vagy a csapatban");
        Report(cs.GetProperty("noPickAction").ValueKind == JsonValueKind.False, "Van pick-lehetőség a telefonnak");

        var (banned, picked, _) = await PlayChampSelectAsync(null, ct);
        Report(!banned && picked, "Csak pick kör volt, és végigment");

        state = await WaitStateAsync(s => TimerPhase(s) == "FINALIZATION", 15, ct);
        Report(state is not null, "Finalizáció elérve");
        if (state is null) return;
        Report(state.Value.GetProperty("champSelect").GetProperty("swapOptions").GetArrayLength() == 0, "Nincs csere-lehetőség (nincs csapattárs)");

        var spells = await CommandAsync(new { cmd = "spells", spell1 = 4, spell2 = 12 }, ct);
        Report(Error(spells) is null, "Spell csere Practice Toolban");
        var rune = await CommandAsync(new { cmd = "rune", pageId = 102 }, ct);
        Report(Error(rune) is null, "Rúnaoldal választás Practice Toolban");
        var save = await CommandAsync(new { cmd = "template-save" }, ct);
        Report(Error(save) is null, "Sablon mentése Practice Toolban");
        await CheckRecommendationsAsync(ct);

        Report(await WaitStateAsync(s => Phase(s) == "InProgress", 40, ct) is not null, "Practice Tool játék indul");
    }

    async Task CheckRecommendationsAsync(CancellationToken ct)
    {
        var mine = await CommandAsync(new { cmd = "rune-recommendations", all = false }, ct);
        var items = Error(mine) is null ? mine.GetProperty("data").GetProperty("items") : default;
        var count = items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
        Report(count > 0, $"Játékbeli rúna-ajánlások ({count} db) {Error(mine)}");
        if (count == 0) return;

        var all = await CommandAsync(new { cmd = "rune-recommendations", all = true }, ct);
        var allItems = all.GetProperty("data").GetProperty("items");
        Report(allItems.GetArrayLength() >= count, $"Bővített ajánlások minden pozícióra ({allItems.GetArrayLength()} db)");

        await CommandAsync(new { cmd = "rune", pageId = 101 }, ct);
        await WaitStateAsync(s => CurrentRune(s) == 101, 5, ct);
        var first = items[0];
        var perkIds = first.GetProperty("perkIds").EnumerateArray().Select(p => p.GetInt32()).ToArray();
        var apply = await CommandAsync(new
        {
            cmd = "rune-apply-recommended",
            primaryStyleId = first.GetProperty("primaryStyleId").GetInt32(),
            subStyleId = first.GetProperty("subStyleId").GetInt32(),
            perkIds,
            label = first.GetProperty("title").GetString()
        }, ct);
        Report(Error(apply) is null, $"Ajánlott rúnaoldal alkalmazása {Error(apply)}");
        Report(await WaitStateAsync(s => CurrentRuneName(s)?.StartsWith("LolRemote") == true, 5, ct) is not null,
            "Az ajánlott oldal lett az aktív (LolRemote oldal)");
        var bad = await CommandAsync(new { cmd = "rune-apply-recommended", primaryStyleId = 8000, subStyleId = 8100, perkIds = new[] { 1, 2 }, label = "x" }, ct);
        Report(Error(bad) is not null, "Hiányos rúnaoldal elutasítva");
    }

    static string? Mode(JsonElement s) =>
        s.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

    static bool? TemplateExists(JsonElement s)
    {
        if (!s.TryGetProperty("champSelect", out var cs) || cs.ValueKind != JsonValueKind.Object) return null;
        var t = cs.GetProperty("template");
        return t.ValueKind == JsonValueKind.Object ? t.GetProperty("exists").GetBoolean() : null;
    }

    static string? CurrentRuneName(JsonElement s)
    {
        if (!s.TryGetProperty("champSelect", out var cs) || cs.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in cs.GetProperty("runePages").EnumerateArray())
            if (p.GetProperty("current").GetBoolean()) return p.GetProperty("name").GetString();
        return null;
    }

    static int? RunePageCount(JsonElement? s)
    {
        if (s is not { } snap || !snap.TryGetProperty("champSelect", out var cs) || cs.ValueKind != JsonValueKind.Object) return null;
        return cs.GetProperty("runePages").GetArrayLength();
    }

    static bool? Setting(JsonElement s, string name) =>
        s.TryGetProperty("settings", out var st) && st.TryGetProperty(name, out var v) ? v.GetBoolean() : null;

    static string? ReadyResponse(JsonElement s) =>
        s.TryGetProperty("readyCheck", out var rc) && rc.ValueKind == JsonValueKind.Object ? rc.GetProperty("response").GetString() : null;

    async Task<(bool Banned, bool Picked, int PickedId)> PlayChampSelectAsync(int? preferredPick, CancellationToken ct)
    {
        var banned = false;
        var picked = false;
        var pickedId = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(150);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var snap = Snapshot();
            if (snap is null || Phase(snap.Value) != "ChampSelect")
            {
                await Task.Delay(200, ct);
                continue;
            }
            var cs = snap.Value.GetProperty("champSelect");
            if (cs.ValueKind != JsonValueKind.Object) break;
            if (cs.GetProperty("timerPhase").GetString() == "FINALIZATION") break;

            var action = cs.GetProperty("myAction");
            if (action.ValueKind != JsonValueKind.Object)
            {
                await Task.Delay(200, ct);
                continue;
            }

            var type = action.GetProperty("type").GetString();
            var champions = await CommandAsync(new { cmd = "champions" }, ct);
            var list = champions.GetProperty("data");
            Report(list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0, $"Championlista ({type}): {(list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0)} db");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) break;

            var options = list.EnumerateArray().ToList();
            var choice = options[Random.Shared.Next(options.Count)];
            if (type == "pick" && preferredPick is int wanted && options.FirstOrDefault(o => o.GetProperty("id").GetInt32() == wanted) is { ValueKind: JsonValueKind.Object } preferred)
                choice = preferred;
            else if (type == "ban" && preferredPick is int keep && choice.GetProperty("id").GetInt32() == keep)
                choice = options.First(o => o.GetProperty("id").GetInt32() != keep);

            var championId = choice.GetProperty("id").GetInt32();
            var name = choice.GetProperty("name").GetString();

            var hover = await CommandAsync(new { cmd = "hover", championId }, ct);
            Report(Error(hover) is null, $"Kijelölés ({type}): {name} {Error(hover)}");
            var hovered = await WaitStateAsync(s => MyActionChampion(s) == championId, 5, ct);
            Report(hovered is not null, $"A kijelölés megjelenik az állapotban ({name})");

            var lockResult = await CommandAsync(new { cmd = "lock" }, ct);
            if (Error(lockResult) is { } lockError && lockError.Contains("elvitték"))
            {
                Info($"{name} közben elfogyott, újrapróbálom: {lockError}");
                continue;
            }
            Report(Error(lockResult) is null, $"Lock ({type}): {name} {Error(lockResult)}");
            if (type == "ban")
            {
                banned = true;
            }
            else
            {
                picked = true;
                pickedId = championId;
            }
            await WaitStateAsync(s => MyActionChampion(s) != championId, 5, ct);
        }
        return (banned, picked, pickedId);
    }

    static string? Error(JsonElement result) =>
        result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    static string? Phase(JsonElement s) => s.TryGetProperty("phase", out var p) ? p.GetString() : null;

    static string? TimerPhase(JsonElement s) =>
        s.TryGetProperty("champSelect", out var cs) && cs.ValueKind == JsonValueKind.Object ? cs.GetProperty("timerPhase").GetString() : null;

    static int? MyActionChampion(JsonElement s)
    {
        if (!s.TryGetProperty("champSelect", out var cs) || cs.ValueKind != JsonValueKind.Object) return null;
        var a = cs.GetProperty("myAction");
        return a.ValueKind == JsonValueKind.Object ? a.GetProperty("championId").GetInt32() : null;
    }

    static long? Spell(JsonElement s, string slot)
    {
        if (!s.TryGetProperty("champSelect", out var cs) || cs.ValueKind != JsonValueKind.Object) return null;
        var sp = cs.GetProperty(slot);
        return sp.ValueKind == JsonValueKind.Object ? sp.GetProperty("id").GetInt64() : null;
    }

    static long? CurrentRune(JsonElement s)
    {
        if (!s.TryGetProperty("champSelect", out var cs) || cs.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in cs.GetProperty("runePages").EnumerateArray())
            if (p.GetProperty("current").GetBoolean()) return p.GetProperty("id").GetInt64();
        return null;
    }

    JsonElement? Snapshot()
    {
        lock (_stateLock) return _snapshot;
    }

    static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null) return value;
            await Task.Delay(200, ct);
        }
        return null;
    }

    async Task<JsonElement?> WaitStateAsync(Func<JsonElement, bool> predicate, int seconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var snap = Snapshot();
            if (snap is { } s && s.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    if (predicate(s)) return s;
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
                {
                }
            }
            await Task.Delay(100, ct);
        }
        return null;
    }

    async Task<bool> AuthAsync(string code, CancellationToken ct)
    {
        _auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync(new { type = "auth", code }, ct);
        var done = await Task.WhenAny(_auth.Task, Task.Delay(5000, ct));
        return done == _auth.Task && _auth.Task.Result;
    }

    async Task<JsonElement> CommandAsync(object args, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _results[id] = tcs;
        var payload = JsonSerializer.SerializeToElement(args);
        var merged = new Dictionary<string, object?> { ["type"] = "cmd", ["id"] = id };
        foreach (var p in payload.EnumerateObject()) merged[p.Name] = p.Value;
        await SendAsync(merged, ct);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10000, ct));
        _results.TryRemove(id, out _);
        if (done == tcs.Task) return tcs.Task.Result;
        return JsonSerializer.SerializeToElement(new { error = "időtúllépés" });
    }

    async Task SendAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await _socket!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket!.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                using var doc = JsonDocument.Parse(message.ToArray());
                message.SetLength(0);
                var root = doc.RootElement.Clone();
                switch (root.GetProperty("type").GetString())
                {
                    case "auth":
                        _auth?.TrySetResult(root.GetProperty("ok").GetBoolean());
                        break;
                    case "result":
                        if (_results.TryGetValue(root.GetProperty("id").GetInt32(), out var tcs)) tcs.TrySetResult(root);
                        break;
                    case "state":
                        if (root.TryGetProperty("snapshot", out var snap))
                        {
                            lock (_stateLock) _snapshot = snap;
                        }
                        break;
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
