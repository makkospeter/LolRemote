using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace LolRemote.Companion;

public sealed class ChampSelectController
{
    static readonly long[] RiftSpellIds = { 4, 14, 12, 7, 11, 3, 21, 6, 1 };
    static readonly HashSet<string> SwapVerbs = new() { "request", "accept", "decline", "cancel" };
    const string TemplatePagePrefix = "LolRemote";

    readonly LcuConnection _lcu;
    readonly EventLog _log;
    readonly Settings _settings;
    readonly TemplateStore _templates;
    readonly HashSet<int> _cancelledRequests = new();
    int _autoAppliedFor;
    readonly ConcurrentDictionary<int, byte[]> _iconCache = new();
    Dictionary<int, string> _championNames = new();
    Dictionary<int, string> _championAliases = new();
    Dictionary<long, string> _spellNames = new();
    Dictionary<int, string> _perkNames = new();
    Dictionary<int, string> _styleNames = new();
    DateTime _readyCheckReceivedAt;
    DateTime _sessionReceivedAt;

    public string Phase { get; private set; } = "None";
    public string? GameMode { get; private set; }
    public int MapId { get; private set; } = 11;
    public Action<string, JsonElement>? DebugWriter { get; set; }
    public bool IsCustomGame { get; private set; }
    public string? QueueName { get; private set; }

    public string? ModeLabel =>
        GameMode == "PRACTICETOOL" ? "Practice Tool"
        : IsCustomGame ? "Egyéni játék"
        : string.IsNullOrWhiteSpace(QueueName) ? null : QueueName;

    public bool NoPickAvailable
    {
        get
        {
            var s = Session;
            var me = Me;
            if (s is null || me is null || me.ChampionId != 0 || MyCurrentAction() is not null) return false;
            return !s.Actions.SelectMany(g => g).Any(a => a.Type == "pick" && a.ActorCellId == s.LocalPlayerCellId);
        }
    }
    public ReadyCheck? ReadyCheck { get; private set; }
    public ChampSelectSession? Session { get; private set; }
    public List<(long Id, string Name)> Spells { get; private set; } = new();
    public List<RunePage> RunePages { get; private set; } = new();

    public event Action? Changed;

    public ChampSelectController(LcuConnection lcu, EventLog log, Settings settings, TemplateStore templates)
    {
        _lcu = lcu;
        _log = log;
        _settings = settings;
        _templates = templates;
        _lcu.EventReceived += OnEvent;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var champions = await _lcu.GetAsync<List<ChampionSummary>>("/lol-game-data/assets/v1/champion-summary.json", ct) ?? new();
        _championNames = champions.Where(c => c.Id > 0).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name);
        _championAliases = champions.Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Alias)).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Alias!);

        var spells = await _lcu.GetAsync<List<SummonerSpell>>("/lol-game-data/assets/v1/summoner-spells.json", ct) ?? new();
        _spellNames = spells.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
        Spells = RiftSpellIds.Where(id => _spellNames.ContainsKey(id)).Select(id => (id, _spellNames[id])).ToList();

        var perks = await _lcu.GetAsync<List<NamedItem>>("/lol-perks/v1/perks", ct) ?? new();
        _perkNames = perks.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Name);
        var styles = await _lcu.GetAsync<List<NamedItem>>("/lol-perks/v1/styles", ct) ?? new();
        _styleNames = styles.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
        RunePages = await _lcu.GetAsync<List<RunePage>>("/lol-perks/v1/pages", ct) ?? new();

        SetGameflowSession(await _lcu.GetAsync<JsonElement>("/lol-gameflow/v1/session", ct));
        Phase = await _lcu.GetAsync<string>("/lol-gameflow/v1/gameflow-phase", ct) ?? "None";
        SetReadyCheck(await _lcu.GetAsync<ReadyCheck>("/lol-matchmaking/v1/ready-check", ct));
        SetSession(await _lcu.GetAsync<ChampSelectSession>("/lol-champ-select/v1/session", ct), false);
        _log.Add($"Kapcsolódva a klienshez. Jelenlegi állapot: {PhaseText(Phase)}", ConsoleColor.Green);
    }

    void OnEvent(string uri, string eventType, JsonElement data)
    {
        var deleted = eventType == "Delete";
        try
        {
            switch (uri)
            {
                case "/lol-gameflow/v1/gameflow-phase":
                    var phase = !deleted && data.ValueKind == JsonValueKind.String ? data.GetString() ?? "None" : "None";
                    if (phase != Phase) _log.Add($"Állapot: {PhaseText(phase)}");
                    Phase = phase;
                    break;
                case "/lol-gameflow/v1/session":
                    if (deleted) return;
                    var modeBefore = ModeLabel;
                    SetGameflowSession(data);
                    if (ModeLabel == modeBefore) return;
                    if (ModeLabel is not null) _log.Add($"Játékmód: {ModeLabel}", ConsoleColor.DarkGray);
                    break;
                case "/lol-matchmaking/v1/ready-check":
                    SetReadyCheck(deleted ? null : data.Deserialize<ReadyCheck>(LcuConnection.JsonOptions));
                    break;
                case "/lol-champ-select/v1/session":
                    SetSession(deleted ? null : data.Deserialize<ChampSelectSession>(LcuConnection.JsonOptions), true);
                    break;
                case "/lol-perks/v1/pages":
                    if (!deleted) RunePages = data.Deserialize<List<RunePage>>(LcuConnection.JsonOptions) ?? new List<RunePage>();
                    break;
                case "/lol-perks/v1/currentpage":
                    if (!deleted && data.Deserialize<RunePage>(LcuConnection.JsonOptions) is { } page)
                    {
                        var before = CurrentRunePage?.Id;
                        foreach (var p in RunePages) p.Current = p.Id == page.Id;
                        if (before != page.Id) _log.Add($"Rúnaoldal: {RuneSummary(page)}", ConsoleColor.Cyan);
                    }
                    break;
                default:
                    return;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return;
        }
        Changed?.Invoke();
    }

    void SetGameflowSession(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        if (!data.TryGetProperty("gameData", out var gameData) || gameData.ValueKind != JsonValueKind.Object) return;
        IsCustomGame = gameData.TryGetProperty("isCustomGame", out var custom) && custom.ValueKind == JsonValueKind.True;
        if (gameData.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Object)
        {
            GameMode = queue.TryGetProperty("gameMode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
            QueueName = queue.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null;
            if (queue.TryGetProperty("mapId", out var map) && map.ValueKind == JsonValueKind.Number && map.TryGetInt32(out var mapId) && mapId > 0)
                MapId = mapId;
        }
    }

    void SetReadyCheck(ReadyCheck? next)
    {
        var previous = ReadyCheck;
        if (next is not null && previous is null && next.State == "InProgress" && next.PlayerResponse == "None")
            _log.Add("MEGVAN A MECCS! Fogadd el!", ConsoleColor.Yellow);
        if (next is not null && next.PlayerResponse != "None" && next.PlayerResponse != previous?.PlayerResponse)
            _log.Add(next.PlayerResponse == "Accepted" ? "Elfogadtad a meccset." : "Elutasítottad a meccset.", ConsoleColor.Cyan);
        ReadyCheck = next;
        _readyCheckReceivedAt = DateTime.UtcNow;

        var isNew = next is { State: "InProgress", PlayerResponse: "None" } && previous is not { State: "InProgress" };
        if (isNew && _settings.AutoAccept)
        {
            _log.Add("Auto-accept: elfogadás 1,5 mp múlva...", ConsoleColor.Cyan);
            _ = Task.Run(AutoAcceptAsync);
        }
    }

    async Task AutoAcceptAsync()
    {
        await Task.Delay(1500);
        if (!_settings.AutoAccept || ReadyCheck is not { State: "InProgress", PlayerResponse: "None" }) return;
        try
        {
            var error = await AcceptAsync();
            _log.Add(error is null ? "Automatikusan elfogadva." : $"Auto-accept hiba: {error}", error is null ? ConsoleColor.Cyan : ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            _log.Add($"Auto-accept hiba: {ex.Message}", ConsoleColor.Red);
        }
        Changed?.Invoke();
    }

    void SetSession(ChampSelectSession? next, bool logChanges)
    {
        if (next is not null)
        {
            next.Trades ??= new List<SwapEntry>();
            next.PickOrderSwaps ??= new List<SwapEntry>();
        }

        var previous = Session;
        if (logChanges && next is not null)
        {
            var previousDone = previous?.Actions.SelectMany(g => g).Where(a => a.Completed).Select(a => a.Id).ToHashSet() ?? new HashSet<int>();
            foreach (var action in next.Actions.SelectMany(g => g).Where(a => a.Completed && !previousDone.Contains(a.Id)))
                LogAction(next, action);

            var mine = MyActionIn(next);
            if (mine is not null && mine.Id != MyActionIn(previous)?.Id)
                _log.Add(mine.Type == "ban" ? ">>> TE JÖSSZ: BAN" : ">>> TE JÖSSZ: PICK", ConsoleColor.Green);

            if (next.Timer.Phase == "FINALIZATION" && previous?.Timer.Phase != "FINALIZATION")
                _log.Add("Minden pick kész. Még cserélhetsz, és állíthatsz spellt, rúnát.", ConsoleColor.Yellow);

            LogSwapChanges(previous, next);
        }
        if (logChanges && next is null && previous is not null)
            _log.Add("A champ select véget ért.");
        Session = next;
        _sessionReceivedAt = DateTime.UtcNow;

        if (next is null)
        {
            _autoAppliedFor = 0;
            return;
        }
        var locked = MyChampion(next) ?? 0;
        var previousLocked = MyChampion(previous) ?? 0;
        if (locked != 0 && locked != previousLocked && locked != _autoAppliedFor && _settings.AutoApplyTemplate && _templates.Get(locked) is not null)
        {
            _autoAppliedFor = locked;
            _ = Task.Run(async () =>
            {
                try
                {
                    var error = await ApplyTemplateAsync(true);
                    if (error is not null) _log.Add($"Auto-sablon hiba: {error}", ConsoleColor.Red);
                }
                catch (Exception ex)
                {
                    _log.Add($"Auto-sablon hiba: {ex.Message}", ConsoleColor.Red);
                }
                Changed?.Invoke();
            });
        }
    }

    void LogAction(ChampSelectSession s, ChampSelectAction a)
    {
        var isMine = a.ActorCellId == s.LocalPlayerCellId;
        var who = isMine ? "Te" : a.IsAllyAction ? $"Csapattárs ({PositionText(PositionOf(s, a.ActorCellId))})" : "Ellenfél";
        if (a.ChampionId == 0)
        {
            _log.Add($"{who}: nem bannolt{(isMine ? "ál" : "")} senkit.", ConsoleColor.DarkGray);
            return;
        }
        var verb = a.Type == "ban" ? (isMine ? "bannoltad" : "bannolta") : (isMine ? "lockoltad" : "pickelte");
        _log.Add($"{who} {verb}: {ChampionName(a.ChampionId)}", isMine ? ConsoleColor.Cyan : ConsoleColor.DarkGray);
    }

    void LogSwapChanges(ChampSelectSession? previous, ChampSelectSession next)
    {
        var tradeHappened = MyChampion(previous) != MyChampion(next);
        var swapHappened = MyPickActionId(previous) != MyPickActionId(next);
        LogSwapList(previous?.Trades, next.Trades!, next, true, tradeHappened);
        LogSwapList(previous?.PickOrderSwaps, next.PickOrderSwaps!, next, false, swapHappened);
    }

    void LogSwapList(List<SwapEntry>? before, List<SwapEntry> after, ChampSelectSession s, bool isTrade, bool happened)
    {
        var what = isTrade ? "champion cserét" : "pick sorrend cserét";
        foreach (var entry in after)
        {
            var old = before?.FirstOrDefault(e => e.Id == entry.Id)?.State;
            if (entry.State == "RECEIVED" && old != "RECEIVED")
                _log.Add($"{AllyLabel(s, entry.CellId)} {what} kér!", ConsoleColor.Yellow);

            if (old == "SENT" && entry.State != "SENT" && !ConsumeCancelled(entry.Id))
            {
                if (happened)
                    _log.Add(isTrade ? $"Sikeres csere! A championod most: {ChampionName(MyChampion(s) ?? 0)}" : "Sikeres pick sorrend csere!", ConsoleColor.Cyan);
                else
                    _log.Add($"{AllyLabel(s, entry.CellId)} nem fogadta el a cserét.", ConsoleColor.DarkYellow);
            }
        }
    }

    bool ConsumeCancelled(int id)
    {
        lock (_cancelledRequests) return _cancelledRequests.Remove(id);
    }

    static int? MyChampion(ChampSelectSession? s) =>
        s?.MyTeam.FirstOrDefault(m => m.CellId == s.LocalPlayerCellId)?.ChampionId;

    static int? MyPickActionId(ChampSelectSession? s) =>
        s?.Actions.SelectMany(g => g).FirstOrDefault(a => a.Type == "pick" && a.ActorCellId == s.LocalPlayerCellId)?.Id;

    static string PositionOf(ChampSelectSession s, int cellId) =>
        s.MyTeam.Concat(s.TheirTeam).FirstOrDefault(m => m.CellId == cellId)?.AssignedPosition ?? "";

    string AllyLabel(ChampSelectSession s, int cellId)
    {
        var m = s.MyTeam.FirstOrDefault(x => x.CellId == cellId);
        if (m is null) return "Csapattárs";
        var champ = m.ChampionId != 0 ? ChampionName(m.ChampionId) : "még nem pickelt";
        return $"{PositionText(m.AssignedPosition)} ({champ})";
    }

    static ChampSelectAction? MyActionIn(ChampSelectSession? s) =>
        s?.Actions.SelectMany(g => g)
            .FirstOrDefault(a => a.ActorCellId == s.LocalPlayerCellId && a.IsInProgress && !a.Completed);

    public ChampSelectAction? MyCurrentAction() => MyActionIn(Session);

    public TeamMember? Me
    {
        get
        {
            var s = Session;
            return s?.MyTeam.FirstOrDefault(m => m.CellId == s.LocalPlayerCellId);
        }
    }

    public RunePage? CurrentRunePage => RunePages.FirstOrDefault(p => p.Current);

    public bool CanChangeRunes => Session is not null;

    public int? SecondsLeft()
    {
        if (Phase == "ReadyCheck" && ReadyCheck is { PlayerResponse: "None" } rc)
        {
            var elapsed = rc.Timer + (DateTime.UtcNow - _readyCheckReceivedAt).TotalSeconds;
            return Math.Max(0, (int)Math.Ceiling(12 - elapsed));
        }
        if (Session is { } s)
        {
            var left = s.Timer.AdjustedTimeLeftInPhase - (DateTime.UtcNow - _sessionReceivedAt).TotalMilliseconds;
            return Math.Max(0, (int)Math.Ceiling(left / 1000));
        }
        return null;
    }

    public string ChampionName(int id) =>
        id == 0 ? "-" : _championNames.TryGetValue(id, out var name) ? name : $"#{id}";

    public string SpellName(long id) =>
        _spellNames.TryGetValue(id, out var name) ? name : $"#{id}";

    string PerkName(int id) => _perkNames.TryGetValue(id, out var name) ? name : $"#{id}";

    string StyleName(int id) => _styleNames.TryGetValue(id, out var name) ? name : $"#{id}";

    public string RuneSummary(RunePage? page)
    {
        if (page is null) return "-";
        var keystone = page.SelectedPerkIds.Count > 0 ? PerkName(page.SelectedPerkIds[0]) : "-";
        return $"{page.Name} ({keystone}, {StyleName(page.PrimaryStyleId)}/{StyleName(page.SubStyleId)})";
    }

    public static string PositionText(string position) => position switch
    {
        "top" => "Top",
        "jungle" => "Jungle",
        "middle" => "Mid",
        "bottom" => "ADC",
        "utility" => "Support",
        _ => "-"
    };

    public static string PhaseText(string phase) => phase switch
    {
        "None" => "Főmenü",
        "Lobby" => "Lobby",
        "Matchmaking" => "Meccskeresés...",
        "ReadyCheck" => "MEGVAN A MECCS",
        "ChampSelect" => "Champ select",
        "GameStart" or "InProgress" => "Játék folyamatban",
        "WaitingForStats" or "PreEndOfGame" or "EndOfGame" => "Meccs vége",
        _ => phase
    };

    public SwapOption? IncomingRequest()
    {
        var s = Session;
        if (s is null) return null;
        var trade = s.Trades!.FirstOrDefault(t => t.State == "RECEIVED");
        if (trade is not null)
            return new SwapOption($"{AllyLabel(s, trade.CellId)} champion cserét kér", true, trade.Id, "accept");
        var swap = s.PickOrderSwaps!.FirstOrDefault(t => t.State == "RECEIVED");
        if (swap is not null)
            return new SwapOption($"{AllyLabel(s, swap.CellId)} pick sorrend cserét kér", false, swap.Id, "accept");
        return null;
    }

    public List<SwapOption> SwapOptions()
    {
        var result = new List<SwapOption>();
        var s = Session;
        var me = Me;
        if (s is null || me is null) return result;

        foreach (var t in s.Trades!)
        {
            if (t.State == "AVAILABLE")
                result.Add(new SwapOption($"Champion csere: {ChampionName(me.ChampionId)} <-> {AllyLabel(s, t.CellId)}", true, t.Id, "request"));
            else if (t.State == "SENT")
                result.Add(new SwapOption($"Kérés visszavonása (champion csere, {AllyLabel(s, t.CellId)})", true, t.Id, "cancel"));
        }
        foreach (var p in s.PickOrderSwaps!)
        {
            if (p.State == "AVAILABLE")
                result.Add(new SwapOption($"Pick sorrend csere: {AllyLabel(s, p.CellId)}", false, p.Id, "request"));
            else if (p.State == "SENT")
                result.Add(new SwapOption($"Kérés visszavonása (pick sorrend, {AllyLabel(s, p.CellId)})", false, p.Id, "cancel"));
        }
        return result;
    }

    public async Task<List<(int Id, string Name)>> AvailableChampionsAsync()
    {
        var action = MyCurrentAction();
        var path = action?.Type == "ban"
            ? "/lol-champ-select/v1/bannable-champion-ids"
            : "/lol-champ-select/v1/pickable-champion-ids";
        var ids = await _lcu.GetAsync<int[]>(path) ?? Array.Empty<int>();
        return ids.Where(id => id > 0).Select(id => (id, ChampionName(id))).OrderBy(c => c.Item2).ToList();
    }

    public async Task<byte[]?> ChampionIconAsync(int championId)
    {
        if (_iconCache.TryGetValue(championId, out var cached)) return cached;
        var bytes = await _lcu.GetBytesAsync($"/lol-game-data/assets/v1/champion-icons/{championId}.png");
        if (bytes is not null) _iconCache[championId] = bytes;
        return bytes;
    }

    public Settings Settings => _settings;

    public int MyChampionId()
    {
        var me = Me;
        if (me is null) return 0;
        if (me.ChampionId != 0) return me.ChampionId;
        var action = MyCurrentAction();
        if (action is { Type: "pick" } && action.ChampionId != 0) return action.ChampionId;
        return me.ChampionPickIntent;
    }

    public RuneTemplate? MyTemplate()
    {
        var champion = MyChampionId();
        return champion == 0 ? null : _templates.Get(champion);
    }

    public string TemplateSummary(RuneTemplate t) =>
        $"{t.RuneName} · {SpellName(t.Spell1Id)} + {SpellName(t.Spell2Id)}";

    public string? SetSetting(string name, bool value)
    {
        switch (name)
        {
            case "autoAccept":
                _settings.AutoAccept = value;
                _log.Add($"Auto-accept: {(value ? "BE" : "KI")}", ConsoleColor.Cyan);
                break;
            case "autoApplyTemplate":
                _settings.AutoApplyTemplate = value;
                _log.Add($"Sablon automatikus alkalmazása: {(value ? "BE" : "KI")}", ConsoleColor.Cyan);
                break;
            default:
                return "Ismeretlen beállítás.";
        }
        _settings.Save();
        Changed?.Invoke();
        return null;
    }

    public string? SaveTemplate()
    {
        if (Session is null) return "Sablont champ selectben lehet menteni.";
        var champion = MyChampionId();
        if (champion == 0) return "Előbb válassz vagy jelölj ki egy championt.";
        var page = CurrentRunePage;
        if (page is null || page.SelectedPerkIds.Count == 0) return "Nincs kiválasztott rúnaoldal.";
        var me = Me!;
        var runeName = page.Name.StartsWith(TemplatePagePrefix, StringComparison.OrdinalIgnoreCase)
            ? _templates.Get(champion)?.RuneName ?? page.Name
            : page.Name;
        var template = new RuneTemplate
        {
            ChampionId = champion,
            ChampionName = ChampionName(champion),
            RuneName = runeName,
            PrimaryStyleId = page.PrimaryStyleId,
            SubStyleId = page.SubStyleId,
            SelectedPerkIds = page.SelectedPerkIds.ToList(),
            Spell1Id = me.Spell1Id,
            Spell2Id = me.Spell2Id,
            SavedAt = DateTime.Now
        };
        _templates.Set(template);
        _log.Add($"Sablon mentve: {template.ChampionName} – {TemplateSummary(template)}", ConsoleColor.Cyan);
        Changed?.Invoke();
        return null;
    }

    public string? DeleteTemplate()
    {
        var champion = MyChampionId();
        if (champion == 0 || !_templates.Remove(champion)) return "Ehhez a championhoz nincs mentett sablon.";
        _log.Add($"Sablon törölve: {ChampionName(champion)}", ConsoleColor.DarkGray);
        Changed?.Invoke();
        return null;
    }

    public async Task<string?> ApplyTemplateAsync(bool automatic = false)
    {
        if (Session is null) return "Sablont champ selectben lehet alkalmazni.";
        var template = MyTemplate();
        if (template is null) return "Ehhez a championhoz nincs mentett sablon.";

        var me = Me;
        if (me is not null && (me.Spell1Id != template.Spell1Id || me.Spell2Id != template.Spell2Id))
        {
            var spellError = await SendAsync(_lcu.PatchAsync("/lol-champ-select/v1/session/my-selection",
                new { spell1Id = template.Spell1Id, spell2Id = template.Spell2Id }));
            if (spellError is not null) return spellError;
        }

        var runeError = await ApplyRunesAsync(template);
        if (runeError is not null) return runeError;

        _log.Add($"{(automatic ? "Auto-sablon" : "Sablon")} alkalmazva: {template.ChampionName} – {TemplateSummary(template)}", ConsoleColor.Cyan);
        return null;
    }

    async Task<string?> ApplyRunesAsync(RuneTemplate template)
    {
        var pageName = $"{TemplatePagePrefix}: {template.ChampionName}";
        var own = RunePages.FirstOrDefault(p => p.IsEditable && p.Name.StartsWith(TemplatePagePrefix, StringComparison.OrdinalIgnoreCase));
        long pageId;

        if (own is not null)
        {
            var error = await SendAsync(_lcu.PutAsync($"/lol-perks/v1/pages/{own.Id}", new
            {
                id = own.Id,
                name = pageName,
                primaryStyleId = template.PrimaryStyleId,
                subStyleId = template.SubStyleId,
                selectedPerkIds = template.SelectedPerkIds,
                current = true
            }));
            if (error is not null) return error;
            pageId = own.Id;
        }
        else
        {
            using var response = await _lcu.PostAsync("/lol-perks/v1/pages", new
            {
                name = pageName,
                primaryStyleId = template.PrimaryStyleId,
                subStyleId = template.SubStyleId,
                selectedPerkIds = template.SelectedPerkIds,
                current = true
            });
            if (!response.IsSuccessStatusCode)
                return "Nincs szabad rúnaoldal-hely. Nevezz át egy rúnaoldalt a kliensben „LolRemote” névre, és a program azt fogja használni.";
            var created = await response.Content.ReadFromJsonAsync<RunePage>(LcuConnection.JsonOptions);
            pageId = created?.Id ?? 0;
        }

        if (pageId == 0) return "Nem sikerült létrehozni a rúnaoldalt.";
        return await SendAsync(_lcu.PutAsync("/lol-perks/v1/currentpage", pageId));
    }

    static readonly string[] AllPositions = { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" };
    readonly ConcurrentDictionary<string, List<RecommendedRunes>> _recommendationCache = new();

    public string MyPositionKey() => Me?.AssignedPosition switch
    {
        "top" => "TOP",
        "jungle" => "JUNGLE",
        "middle" => "MIDDLE",
        "bottom" => "BOTTOM",
        "utility" => "UTILITY",
        _ => ""
    };

    public async Task<(string? Error, object? Data)> RuneRecommendationsAsync(bool all)
    {
        if (Session is null) return ("Ajánlásokat champ selectben lehet kérni.", null);
        var champion = MyChampionId();
        if (champion == 0) return ("Előbb jelölj ki egy championt.", null);

        var mine = MyPositionKey();
        var positions = all || mine == ""
            ? AllPositions.OrderBy(p => p == mine ? 0 : 1).ToList()
            : new List<string> { mine };

        var items = new List<object>();
        foreach (var position in positions)
        {
            var list = await LoadRecommendationsAsync(champion, position);
            foreach (var r in list)
            {
                items.Add(new
                {
                    position,
                    positionText = PositionText(position.ToLowerInvariant()),
                    isMine = position == mine,
                    title = r.Title,
                    detail = r.Detail,
                    primaryStyleId = r.PrimaryStyleId,
                    subStyleId = r.SubStyleId,
                    perkIds = r.PerkIds
                });
            }
        }
        return (null, new { championName = ChampionName(champion), myPosition = mine, all = all || mine == "", items });
    }

    async Task<List<RecommendedRunes>> LoadRecommendationsAsync(int championId, string position)
    {
        var key = $"{championId}|{position}|{MapId}";
        if (_recommendationCache.TryGetValue(key, out var cached)) return cached;
        JsonElement raw;
        try
        {
            raw = await _lcu.GetAsync<JsonElement>($"/lol-perks/v1/recommended-pages/champion/{championId}/position/{position}/map/{MapId}");
        }
        catch (Exception)
        {
            return new List<RecommendedRunes>();
        }
        DebugWriter?.Invoke($"recommended-pages-{championId}-{position}-{MapId}", raw);
        var parsed = ParseRecommendations(raw, position);
        if (parsed.Count > 0) _recommendationCache[key] = parsed;
        return parsed;
    }

    List<RecommendedRunes> ParseRecommendations(JsonElement raw, string position)
    {
        var result = new List<RecommendedRunes>();
        if (raw.ValueKind != JsonValueKind.Array) return result;
        foreach (var page in raw.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object) continue;
            var primary = IntProp(page, "primaryPerkStyleId") ?? IntProp(page, "primaryStyleId") ?? 0;
            var sub = IntProp(page, "secondaryPerkStyleId") ?? IntProp(page, "subStyleId") ?? 0;
            var perkIds = new List<int>();
            var source = page.TryGetProperty("perks", out var perks) && perks.ValueKind == JsonValueKind.Array ? perks
                : page.TryGetProperty("selectedPerkIds", out var selected) && selected.ValueKind == JsonValueKind.Array ? selected
                : default;
            if (source.ValueKind == JsonValueKind.Array)
            {
                foreach (var perk in source.EnumerateArray())
                {
                    if (perk.ValueKind == JsonValueKind.Number && perk.TryGetInt32(out var id)) perkIds.Add(id);
                    else if (perk.ValueKind == JsonValueKind.Object && IntProp(perk, "id") is int objectId) perkIds.Add(objectId);
                }
            }
            if (perkIds.Count == 6) perkIds.AddRange(new[] { 5008, 5008, 5011 });
            if (primary == 0 || sub == 0 || perkIds.Count != 9) continue;

            var keystone = page.TryGetProperty("keystone", out var ks) && ks.ValueKind == JsonValueKind.Object
                && ks.TryGetProperty("name", out var ksName) && ksName.ValueKind == JsonValueKind.String
                ? ksName.GetString()!
                : PerkName(perkIds[0]);
            var detail = $"{StyleName(primary)} / {StyleName(sub)} · {string.Join(", ", perkIds.Skip(1).Take(5).Select(PerkName))}";
            result.Add(new RecommendedRunes(position, keystone, detail, primary, sub, perkIds));
        }
        return result;
    }

    static int? IntProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    public async Task<string?> ApplyRecommendedAsync(int primaryStyleId, int subStyleId, List<int> perkIds, string label)
    {
        if (Session is null) return "Rúnaoldalt csak champ selectben lehet választani.";
        if (perkIds.Count != 9 || primaryStyleId == 0 || subStyleId == 0) return "Érvénytelen rúnaoldal.";
        var champion = MyChampionId();
        var name = champion == 0 ? label : $"{ChampionName(champion)} {label}";
        var template = new RuneTemplate
        {
            ChampionId = champion,
            ChampionName = name,
            PrimaryStyleId = primaryStyleId,
            SubStyleId = subStyleId,
            SelectedPerkIds = perkIds
        };
        var error = await ApplyRunesAsync(template);
        if (error is null) _log.Add($"Ajánlott rúnaoldal beállítva: {name}", ConsoleColor.Cyan);
        return error;
    }

    public OpggCounters? Counters { get; set; }

    string? MyOpggPosition => OpggCounters.ToOpggPosition(Me?.AssignedPosition ?? "");

    string? AliasOf(int championId) => _championAliases.TryGetValue(championId, out var alias) ? alias : null;

    string LocalName(int championId, string fallback) =>
        _championNames.TryGetValue(championId, out var local) ? local : fallback;

    static double Smoothed(int wins, int games) => (wins + 25.0) / (games + 50.0);

    async Task<HashSet<int>> PickableIdsAsync()
    {
        if (Session is null) return new HashSet<int>();
        try
        {
            var ids = await _lcu.GetAsync<int[]>("/lol-champ-select/v1/pickable-champion-ids") ?? System.Array.Empty<int>();
            return ids.ToHashSet();
        }
        catch (Exception)
        {
            return new HashSet<int>();
        }
    }

    public async Task<(string? Error, object? Data)> CountersAsync(int championId)
    {
        if (Counters is null) return ("A counter-adatok nem érhetők el.", null);
        if (championId <= 0) return ("Nincs kiválasztott champion.", null);
        var name = ChampionName(championId);
        var isMe = championId == MyChampionId();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var data = await Counters.MatchupAsync(championId, name, AliasOf(championId), championId, name, AliasOf(championId), MyOpggPosition, timeout.Token);
            var maxGames = data.Counters.Count == 0 ? 0 : data.Counters.Max(c => c.Games);
            var minGames = Math.Max(20, (int)(maxGames * 0.03));
            var pickable = isMe ? new HashSet<int>() : await PickableIdsAsync();
            var items = data.Counters
                .Where(c => c.Games >= minGames)
                .Select(c => new
                {
                    championId = c.ChampionId,
                    name = LocalName(c.ChampionId, c.Name),
                    winRate = Math.Round((1 - c.WinRate) * 100, 1),
                    games = c.Games,
                    score = Smoothed(c.Games - c.Wins, c.Games),
                    pickable = pickable.Contains(c.ChampionId)
                })
                .OrderByDescending(c => c.score)
                .ToList();
            return (null, new
            {
                championId,
                championName = name,
                isMe,
                position = OpggCounters.PositionText(data.Position),
                items,
                source = "OP.GG"
            });
        }
        catch (Exception ex)
        {
            return ($"Counter-adatok nem érhetők el: {ex.Message}", null);
        }
    }

    async Task<int> GuessLaneOpponentAsync(int myChampion, CancellationToken ct)
    {
        if (Session is null || Counters is null) return 0;
        var enemies = Session.TheirTeam.Where(m => m.ChampionId != 0).Select(m => m.ChampionId).Distinct().ToList();
        if (enemies.Count == 0) return 0;
        var position = MyOpggPosition;
        if (position is null)
        {
            var mine = await Counters.InfoAsync(myChampion, ChampionName(myChampion), AliasOf(myChampion), ct);
            position = mine.Positions.FirstOrDefault();
        }
        if (position is null) return 0;
        int best = 0, bestRank = int.MaxValue;
        foreach (var enemy in enemies)
        {
            var info = await Counters.InfoAsync(enemy, ChampionName(enemy), AliasOf(enemy), ct);
            var rank = info.Positions.IndexOf(position);
            if (rank >= 0 && rank < bestRank)
            {
                best = enemy;
                bestRank = rank;
            }
        }
        return best;
    }

    public async Task<(string? Error, object? Data)> BuildAsync(int opponentId)
    {
        if (Session is null) return ("Ajánlást champ selectben lehet kérni.", null);
        var champion = MyChampionId();
        if (champion == 0) return ("Előbb jelölj ki egy championt.", null);
        if (Counters is null) return ("Az OP.GG adatok nem érhetők el.", null);

        var name = ChampionName(champion);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var guessed = false;
            if (opponentId <= 0 || opponentId == champion)
            {
                opponentId = await GuessLaneOpponentAsync(champion, timeout.Token);
                guessed = opponentId != 0;
            }

            MatchupData? matchup = null;
            if (opponentId > 0)
            {
                matchup = await Counters.MatchupAsync(champion, name, AliasOf(champion), opponentId, ChampionName(opponentId), AliasOf(opponentId), MyOpggPosition, timeout.Token);
            }
            var general = matchup is { Runes.Count: > 0, Spells.Count: > 0 }
                ? null
                : await Counters.GeneralBuildAsync(champion, name, AliasOf(champion), MyOpggPosition, timeout.Token);

            var spells = (matchup?.Spells.Count > 0 ? matchup.Spells : general?.Spells ?? new List<SpellStat>())
                .Take(3)
                .Select(sp => new
                {
                    ids = sp.Ids,
                    names = string.Join(" + ", sp.Ids.Select(id => SpellName(id))),
                    games = sp.Games,
                    winRate = sp.Games == 0 ? 0 : Math.Round(100.0 * sp.Wins / sp.Games, 1)
                }).ToList();
            var runes = (matchup?.Runes.Count > 0 ? matchup.Runes : general?.Runes ?? new List<RuneStat>())
                .Take(3)
                .Select(r => new
                {
                    primaryStyleId = r.PrimaryStyleId,
                    subStyleId = r.SubStyleId,
                    perkIds = r.PerkIds,
                    title = PerkName(r.PerkIds[0]),
                    detail = $"{StyleName(r.PrimaryStyleId)} / {StyleName(r.SubStyleId)} · {string.Join(", ", r.PerkIds.Skip(1).Take(5).Select(PerkName))}",
                    games = r.Games,
                    winRate = r.Games == 0 ? 0 : Math.Round(100.0 * r.Wins / r.Games, 1)
                }).ToList();

            var usedMatchup = matchup is not null && (matchup.Runes.Count > 0 || matchup.Spells.Count > 0);
            return (null, new
            {
                championId = champion,
                championName = name,
                position = OpggCounters.PositionText((matchup ?? general)!.Position),
                opponentId = usedMatchup ? opponentId : 0,
                opponentName = usedMatchup ? ChampionName(opponentId) : null,
                opponentGuessed = guessed,
                tip = usedMatchup ? matchup!.Tip : null,
                playStyle = usedMatchup ? matchup!.PlayStyle : null,
                spells,
                runes,
                source = "OP.GG"
            });
        }
        catch (Exception ex)
        {
            return ($"OP.GG ajánlás nem érhető el: {ex.Message}", null);
        }
    }

    public Task<string?> MockStartQueueAsync() => SendAsync(_lcu.PostAsync("/lolremote-mock/queue"));

    public Task<string?> MockExitAsync() => SendAsync(_lcu.PostAsync("/lolremote-mock/exit"));

    public Task<string?> AcceptAsync() => SendAsync(_lcu.PostAsync("/lol-matchmaking/v1/ready-check/accept"));

    public Task<string?> DeclineAsync() => SendAsync(_lcu.PostAsync("/lol-matchmaking/v1/ready-check/decline"));

    public async Task<string?> HoverAsync(int championId)
    {
        var action = MyCurrentAction();
        if (action is null) return "Most nem te jössz.";
        var error = await SendAsync(_lcu.PatchAsync($"/lol-champ-select/v1/session/actions/{action.Id}", new { championId }));
        if (error is null) _log.Add($"Kijelölve: {ChampionName(championId)}. Véglegesítés: LOCK", ConsoleColor.Cyan);
        return error;
    }

    public async Task<string?> LockAsync()
    {
        var action = MyCurrentAction();
        if (action is null) return "Most nem te jössz.";
        if (action.ChampionId == 0) return "Előbb jelölj ki egy championt.";
        var error = await SendAsync(_lcu.PatchAsync($"/lol-champ-select/v1/session/actions/{action.Id}",
            new { championId = action.ChampionId, completed = true }));
        if (error is null) return null;
        var fallback = await SendAsync(_lcu.PostAsync($"/lol-champ-select/v1/session/actions/{action.Id}/complete"));
        return fallback is null ? null : error;
    }

    public async Task<string?> SetSpellsAsync(long spell1, long spell2)
    {
        if (Session is null) return "Spellt csak champ selectben lehet állítani.";
        if (spell1 == spell2) return "A két spell nem lehet ugyanaz.";
        var error = await SendAsync(_lcu.PatchAsync("/lol-champ-select/v1/session/my-selection", new { spell1Id = spell1, spell2Id = spell2 }));
        if (error is null) _log.Add($"Spellek beállítva: {SpellName(spell1)} + {SpellName(spell2)}", ConsoleColor.Cyan);
        return error;
    }

    public Task<string?> SetRunePageAsync(long pageId)
    {
        if (!CanChangeRunes) return Task.FromResult<string?>("Rúnaoldalt csak champ selectben lehet választani.");
        return SendAsync(_lcu.PutAsync("/lol-perks/v1/currentpage", pageId));
    }

    public async Task<string?> SwapAsync(bool isTrade, int id, string verb)
    {
        if (!SwapVerbs.Contains(verb)) return "Ismeretlen csere-művelet.";
        if (Session is null) return "Cserélni csak champ selectben lehet.";
        if (verb == "cancel")
        {
            lock (_cancelledRequests) _cancelledRequests.Add(id);
        }
        var kind = isTrade ? "trades" : "swaps";
        var error = await SendAsync(_lcu.PostAsync($"/lol-champ-select/v1/session/{kind}/{id}/{verb}"));
        var what = isTrade ? "champion csere" : "pick sorrend csere";
        if (error is not null)
        {
            if (verb == "cancel") ConsumeCancelled(id);
            return error;
        }
        switch (verb)
        {
            case "request": _log.Add($"Kérés elküldve ({what}), várjuk a választ...", ConsoleColor.Cyan); break;
            case "cancel": _log.Add($"Kérés visszavonva ({what}).", ConsoleColor.DarkGray); break;
            case "accept": _log.Add($"Elfogadtad: {what}.", ConsoleColor.Cyan); break;
            case "decline": _log.Add($"Elutasítottad: {what}.", ConsoleColor.DarkGray); break;
        }
        return null;
    }

    public object Snapshot()
    {
        var s = Session;
        var me = Me;
        var mine = MyCurrentAction();
        var rc = ReadyCheck;
        var incoming = IncomingRequest();
        var templateChampion = s is null ? 0 : MyChampionId();
        var template = templateChampion == 0 ? null : _templates.Get(templateChampion);

        return new
        {
            settings = new { autoAccept = _settings.AutoAccept, autoApplyTemplate = _settings.AutoApplyTemplate },
            phase = Phase,
            phaseText = PhaseText(Phase),
            mode = ModeLabel,
            secondsLeft = SecondsLeft(),
            readyCheck = Phase == "ReadyCheck" && rc is not null ? new { response = rc.PlayerResponse } : null,
            spells = Spells.Select(x => new { id = x.Id, name = x.Name }).ToList(),
            champSelect = s is null ? null : new
            {
                timerPhase = s.Timer.Phase,
                myChampionId = MyChampionId(),
                noPickAction = NoPickAvailable,
                myAction = mine is null ? null : new
                {
                    type = mine.Type,
                    championId = mine.ChampionId,
                    championName = mine.ChampionId == 0 ? null : ChampionName(mine.ChampionId)
                },
                myTeam = s.MyTeam.Select(m => new
                {
                    cellId = m.CellId,
                    position = PositionText(m.AssignedPosition),
                    championId = m.ChampionId,
                    championName = m.ChampionId == 0 ? null : ChampionName(m.ChampionId),
                    intentId = m.ChampionPickIntent,
                    intentName = m.ChampionPickIntent == 0 ? null : ChampionName(m.ChampionPickIntent),
                    isMe = m.CellId == s.LocalPlayerCellId
                }).ToList(),
                theirTeam = s.TheirTeam.Select(m => new
                {
                    championId = m.ChampionId,
                    championName = m.ChampionId == 0 ? null : ChampionName(m.ChampionId)
                }).ToList(),
                myBans = (s.Bans?.MyTeamBans ?? new List<int>()).Where(id => id != 0).Select(ChampionName).ToList(),
                theirBans = (s.Bans?.TheirTeamBans ?? new List<int>()).Where(id => id != 0).Select(ChampionName).ToList(),
                spell1 = me is null ? null : new { id = me.Spell1Id, name = SpellName(me.Spell1Id) },
                spell2 = me is null ? null : new { id = me.Spell2Id, name = SpellName(me.Spell2Id) },
                incoming = incoming is null ? null : new { label = incoming.Label, isTrade = incoming.IsTrade, id = incoming.Id },
                swapOptions = SwapOptions().Select(o => new { label = o.Label, isTrade = o.IsTrade, id = o.Id, verb = o.Verb }).ToList(),
                runePages = RunePages.Select(p => new { id = p.Id, name = p.Name, summary = RuneSummary(p), current = p.Current }).ToList(),
                template = templateChampion == 0 ? null : new
                {
                    championId = templateChampion,
                    championName = ChampionName(templateChampion),
                    exists = template is not null,
                    summary = template is null ? null : TemplateSummary(template)
                }
            }
        };
    }

    static async Task<string?> SendAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        if (response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var message))
                body = message.GetString() ?? body;
        }
        catch (JsonException)
        {
        }
        return $"Hiba ({(int)response.StatusCode}): {body}";
    }
}

public record RecommendedRunes(string Position, string Title, string Detail, int PrimaryStyleId, int SubStyleId, List<int> PerkIds);
