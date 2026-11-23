using System.Text.Json;

namespace LolRemote.MockLcu;

public static class Out
{
    static readonly object Sync = new();

    public static void Line(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (Sync)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }

    public static void Log(string text, ConsoleColor color = ConsoleColor.Gray) =>
        Line($"[{DateTime.Now:HH:mm:ss}] {text}", color);
}

public class MockEngine
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static readonly Dictionary<int, string> Champions = new()
    {
        [1] = "Annie", [2] = "Olaf", [3] = "Galio", [4] = "Twisted Fate", [5] = "Xin Zhao",
        [6] = "Urgot", [7] = "LeBlanc", [8] = "Vladimir", [9] = "Fiddlesticks", [10] = "Kayle",
        [11] = "Master Yi", [12] = "Alistar", [13] = "Ryze", [14] = "Sion", [15] = "Sivir",
        [16] = "Soraka", [17] = "Teemo", [18] = "Tristana", [19] = "Warwick", [20] = "Nunu & Willump",
        [21] = "Miss Fortune", [22] = "Ashe", [23] = "Tryndamere", [24] = "Jax", [25] = "Morgana",
        [26] = "Zilean", [27] = "Singed", [28] = "Evelynn", [29] = "Twitch", [30] = "Karthus",
        [64] = "Lee Sin", [81] = "Ezreal", [99] = "Lux", [103] = "Ahri", [157] = "Yasuo",
        [222] = "Jinx", [412] = "Thresh"
    };

    public static readonly Dictionary<long, string> Spells = new()
    {
        [4] = "Flash", [14] = "Ignite", [12] = "Teleport", [7] = "Heal", [11] = "Smite",
        [3] = "Exhaust", [21] = "Barrier", [6] = "Ghost", [1] = "Cleanse"
    };

    const string PhaseUri = "/lol-gameflow/v1/gameflow-phase";
    const string ReadyCheckUri = "/lol-matchmaking/v1/ready-check";
    const string SessionUri = "/lol-champ-select/v1/session";

    static readonly int[] AllChampionIds = Champions.Keys.ToArray();
    static readonly string[] Positions = { "top", "jungle", "middle", "bottom", "utility" };
    static readonly long[] SpellPool = { 4, 14, 12, 7, 11, 3, 21, 6 };

    static readonly TimeSpan MatchFoundDelay = TimeSpan.FromSeconds(3);
    static readonly TimeSpan ReadyCheckLength = TimeSpan.FromSeconds(12);
    static readonly TimeSpan AllAcceptedDelay = TimeSpan.FromSeconds(1.5);
    static readonly TimeSpan BanPickLength = TimeSpan.FromSeconds(30);
    static readonly TimeSpan FinalizationLength = TimeSpan.FromSeconds(20);

    readonly object _lock = new();
    readonly Random _rng = new();
    readonly WebSocketHub _hub;
    readonly Dictionary<int, DateTime> _botDue = new();
    readonly Dictionary<int, DateTime> _swapResponses = new();

    string _phase = "None";
    DateTime _matchFoundAt;
    ReadyCheck? _readyCheck;
    DateTime _readyCheckStartedAt;
    DateTime? _acceptedAt;
    ChampSelectSession? _session;
    int _groupIndex;
    DateTime _phaseStartedAt;
    DateTime _phaseDeadline;

    public MockEngine(WebSocketHub hub) => _hub = hub;

    public bool Practice { get; set; }

    public object GameflowSession() => new
    {
        phase = Phase,
        gameData = new
        {
            isCustomGame = Practice,
            queue = new
            {
                gameMode = Practice ? "PRACTICETOOL" : "CLASSIC",
                description = Practice ? "Practice Tool" : "Ranked Solo/Duo (mock)"
            }
        }
    };

    public string Phase
    {
        get { lock (_lock) return _phase; }
    }

    static string Name(int championId) =>
        Champions.TryGetValue(championId, out var name) ? name : $"#{championId}";

    static string PositionName(string position) => position switch
    {
        "top" => "Top",
        "jungle" => "Jungle",
        "middle" => "Mid",
        "bottom" => "ADC",
        "utility" => "Support",
        _ => position
    };

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Tick();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void StartQueue()
    {
        lock (_lock)
        {
            if (_phase is not ("None" or "Lobby" or "InProgress"))
            {
                Out.Log("Már folyamatban van egy meccs. Előbb nyomj [R]-t (alaphelyzet).", ConsoleColor.DarkYellow);
                return;
            }
            if (Practice)
            {
                Out.Log("Practice Tool: champ select indul (nincs ready check).");
                StartChampSelect(DateTime.UtcNow);
                return;
            }
            SetPhase("Matchmaking");
            _matchFoundAt = DateTime.UtcNow + MatchFoundDelay;
            Out.Log("Meccskeresés elindítva, 3 mp múlva meglesz a meccs.");
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            ClearReadyCheck();
            ClearSession();
            SetPhase("None");
            Out.Log("Alaphelyzet (főmenü).");
        }
    }

    public void PrintStatus()
    {
        lock (_lock)
        {
            Out.Log($"Fázis: {_phase}", ConsoleColor.White);
            if (_session is null) return;
            var me = FindMember(_session.LocalPlayerCellId)!;
            Out.Log($"Játékos: {PositionName(me.AssignedPosition)}, champion: {Name(me.ChampionId)}, kijelölve: {Name(me.ChampionPickIntent)}, spellek: {Spells[me.Spell1Id]} + {Spells[me.Spell2Id]}", ConsoleColor.White);
            Out.Log($"Bannok: {string.Join(", ", _session.Bans.MyTeamBans.Concat(_session.Bans.TheirTeamBans).Where(id => id != 0).Select(Name))}", ConsoleColor.White);
        }
    }

    public string? GetReadyCheckJson()
    {
        lock (_lock)
        {
            if (_readyCheck is null) return null;
            _readyCheck.Timer = (DateTime.UtcNow - _readyCheckStartedAt).TotalSeconds;
            return JsonSerializer.Serialize(_readyCheck, JsonOptions);
        }
    }

    public string? GetSessionJson()
    {
        lock (_lock)
        {
            if (_session is null) return null;
            UpdateTimer();
            RefreshSwapStates();
            return JsonSerializer.Serialize(_session, JsonOptions);
        }
    }

    public int[] GetAvailableIds(string type)
    {
        lock (_lock)
        {
            if (_session is null || StrictLock) return AllChampionIds.ToArray();
            return AllChampionIds.Where(c => IsAvailable(c, type)).ToArray();
        }
    }

    public bool AcceptReadyCheck()
    {
        lock (_lock)
        {
            if (_readyCheck is null || _readyCheck.PlayerResponse != "None") return false;
            _readyCheck.PlayerResponse = "Accepted";
            _acceptedAt = DateTime.UtcNow;
            PublishReadyCheck("Update");
            Out.Log("A játékos ELFOGADTA a meccset.", ConsoleColor.Cyan);
            return true;
        }
    }

    public bool DeclineReadyCheck()
    {
        lock (_lock)
        {
            if (_readyCheck is null || _readyCheck.PlayerResponse != "None") return false;
            _readyCheck.PlayerResponse = "Declined";
            PublishReadyCheck("Update");
            Out.Log("A játékos ELUTASÍTOTTA a meccset.", ConsoleColor.Red);
            ClearReadyCheck();
            SetPhase("Lobby");
            return true;
        }
    }

    public bool StrictLock { get; set; }

    public string? PatchAction(int actionId, int? championId, bool? completed, bool fromComplete = false)
    {
        if (StrictLock && !fromComplete && completed == true) completed = null;
        lock (_lock)
        {
            if (_session is null) return "Nincs champ select.";
            var action = _session.Actions.SelectMany(g => g).FirstOrDefault(a => a.Id == actionId);
            if (action is null) return "Nincs ilyen action.";
            if (action.ActorCellId != _session.LocalPlayerCellId) return "Ez nem a te actionöd.";
            if (!action.IsInProgress || action.Completed) return "Most nem te jössz.";

            if (championId is int champ)
            {
                if (!IsAvailable(champ, action.Type)) return $"{Name(champ)} nem választható.";
                action.ChampionId = champ;
                if (action.Type == "pick") FindMember(action.ActorCellId)!.ChampionPickIntent = champ;
                if (completed != true) Out.Log($"A játékos kijelölte: {Name(champ)}", ConsoleColor.DarkCyan);
            }

            if (completed == true)
            {
                if (action.ChampionId == 0) return "Előbb jelölj ki egy championt.";
                if (!IsAvailable(action.ChampionId, action.Type)) return "Ezt a championt közben elvitték, válassz mást.";
                Complete(action);
                Out.Log($"A játékos {(action.Type == "ban" ? "BANNOLTA" : "LOCKOLTA")}: {Name(action.ChampionId)}", ConsoleColor.Cyan);
                CheckGroupDone(DateTime.UtcNow);
            }

            PublishSession();
            return null;
        }
    }

    public string? SetSpells(long? spell1, long? spell2)
    {
        lock (_lock)
        {
            if (_session is null) return "Nincs champ select.";
            var me = FindMember(_session.LocalPlayerCellId)!;
            var s1 = spell1 ?? me.Spell1Id;
            var s2 = spell2 ?? me.Spell2Id;
            if (s1 == s2) return "A két spell nem lehet ugyanaz.";
            if (!Spells.ContainsKey(s1) || !Spells.ContainsKey(s2)) return "Ismeretlen spell.";
            me.Spell1Id = s1;
            me.Spell2Id = s2;
            Out.Log($"A játékos spelljei: {Spells[s1]} + {Spells[s2]}", ConsoleColor.Cyan);
            PublishSession();
            return null;
        }
    }

    public string? SwapCommand(bool isTrade, int id, string verb)
    {
        lock (_lock)
        {
            if (_session is null) return "Nincs champ select.";
            RefreshSwapStates();
            var list = isTrade ? _session.Trades : _session.PickOrderSwaps;
            var entry = list.FirstOrDefault(e => e.Id == id);
            if (entry is null) return "Nincs ilyen csere.";
            var label = isTrade ? "champion cserét" : "pick sorrend cserét";
            var who = $"Csapattárs ({PositionName(FindMember(entry.CellId)!.AssignedPosition)})";

            switch (verb)
            {
                case "request":
                    if (entry.State != "AVAILABLE") return "Ez a csere most nem kérhető.";
                    if (list.Any(e => e.State == "SENT")) return "Már van egy függő kérésed.";
                    entry.State = "SENT";
                    _swapResponses[entry.Id] = DateTime.UtcNow + TimeSpan.FromSeconds(2 + _rng.NextDouble() * 3);
                    Out.Log($"A játékos {label} kér tőle: {who}", ConsoleColor.Cyan);
                    break;
                case "cancel":
                    if (entry.State != "SENT") return "Nincs visszavonható kérés.";
                    entry.State = "AVAILABLE";
                    _swapResponses.Remove(entry.Id);
                    Out.Log($"A játékos visszavonta a kérést ({who}).", ConsoleColor.DarkCyan);
                    break;
                case "accept":
                    if (entry.State != "RECEIVED") return "Nincs elfogadható kérés.";
                    entry.State = "AVAILABLE";
                    if (isTrade) ExecuteTrade(entry.CellId); else ExecutePickOrderSwap(entry.CellId);
                    break;
                case "decline":
                    if (entry.State != "RECEIVED") return "Nincs elutasítható kérés.";
                    entry.State = "AVAILABLE";
                    Out.Log($"A játékos elutasította: {who} {label}.", ConsoleColor.DarkCyan);
                    break;
                default:
                    return "Ismeretlen művelet.";
            }

            PublishSession();
            return null;
        }
    }

    void Tick()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            switch (_phase)
            {
                case "Matchmaking" when now >= _matchFoundAt:
                    StartReadyCheck(now);
                    break;
                case "ReadyCheck":
                    TickReadyCheck(now);
                    break;
                case "ChampSelect":
                    TickChampSelect(now);
                    break;
            }
        }
    }

    void StartReadyCheck(DateTime now)
    {
        _readyCheck = new ReadyCheck();
        _readyCheckStartedAt = now;
        _acceptedAt = null;
        SetPhase("ReadyCheck");
        PublishReadyCheck("Create");
        Out.Log("MEGVAN A MECCS! A játékosnak 12 mp-e van elfogadni (társprogram: [A]).", ConsoleColor.Yellow);
    }

    void TickReadyCheck(DateTime now)
    {
        if (_readyCheck is null) return;
        if (_acceptedAt is DateTime accepted && now >= accepted + AllAcceptedDelay)
        {
            _readyCheck.State = "EveryoneReady";
            PublishReadyCheck("Update");
            ClearReadyCheck();
            StartChampSelect(now);
        }
        else if (_acceptedAt is null && now >= _readyCheckStartedAt + ReadyCheckLength)
        {
            Out.Log("Lejárt a ready check, a játékos nem fogadta el. Vissza a lobbyba.", ConsoleColor.Red);
            ClearReadyCheck();
            SetPhase("Lobby");
        }
    }

    void StartChampSelect(DateTime now)
    {
        var nextId = 1;
        if (Practice)
        {
            var practice = new ChampSelectSession
            {
                GameId = _rng.NextInt64(1_000_000_000, 9_999_999_999),
                LocalPlayerCellId = 0
            };
            practice.MyTeam.Add(new TeamMember { CellId = 0, Team = 1, AssignedPosition = "", Spell1Id = 4, Spell2Id = 14, SummonerId = 100 });
            practice.Actions.Add(new List<ChampSelectAction> { new() { Id = nextId, ActorCellId = 0, Type = "pick", IsAllyAction = true } });
            _session = practice;
            _groupIndex = -1;
            SetPhase("ChampSelect");
            PublishGameflow();
            Out.Log("CHAMP SELECT (Practice Tool): csak a játékos választ.", ConsoleColor.Yellow);
            AdvanceGroup(now);
            return;
        }

        var session = new ChampSelectSession
        {
            GameId = _rng.NextInt64(1_000_000_000, 9_999_999_999),
            LocalPlayerCellId = _rng.Next(0, 5)
        };

        for (var cell = 0; cell < 10; cell++)
        {
            var member = new TeamMember
            {
                CellId = cell,
                Team = cell < 5 ? 1 : 2,
                AssignedPosition = Positions[cell % 5],
                Spell1Id = 4,
                Spell2Id = cell % 5 == 1 ? 11 : SpellPool[_rng.Next(1, SpellPool.Length)],
                SummonerId = cell < 5 ? 100 + cell : 0
            };
            (cell < 5 ? session.MyTeam : session.TheirTeam).Add(member);
        }

        var swapId = 1;
        foreach (var ally in session.MyTeam.Where(m => m.CellId != session.LocalPlayerCellId))
        {
            session.Trades.Add(new SwapEntry { Id = swapId, CellId = ally.CellId });
            session.PickOrderSwaps.Add(new SwapEntry { Id = swapId + 10, CellId = ally.CellId });
            swapId++;
        }

        session.Actions.Add(Enumerable.Range(0, 10).Select(cell => new ChampSelectAction
        {
            Id = nextId++,
            ActorCellId = cell,
            Type = "ban",
            IsAllyAction = cell < 5
        }).ToList());

        int[][] pickOrder = { new[] { 0 }, new[] { 5, 6 }, new[] { 1, 2 }, new[] { 7, 8 }, new[] { 3, 4 }, new[] { 9 } };
        foreach (var group in pickOrder)
        {
            session.Actions.Add(group.Select(cell => new ChampSelectAction
            {
                Id = nextId++,
                ActorCellId = cell,
                Type = "pick",
                IsAllyAction = cell < 5
            }).ToList());
        }

        _session = session;
        _groupIndex = -1;
        SetPhase("ChampSelect");
        var me = FindMember(session.LocalPlayerCellId)!;
        Out.Log($"CHAMP SELECT indul. A játékos pozíciója: {PositionName(me.AssignedPosition)}.", ConsoleColor.Yellow);
        AdvanceGroup(now);

        if (_rng.NextDouble() < 0.4)
        {
            BotRequest(session.PickOrderSwaps, "pick sorrend cserét");
            PublishSession();
        }
    }

    void AdvanceGroup(DateTime now)
    {
        var s = _session!;
        if (_groupIndex >= 0)
        {
            foreach (var a in s.Actions[_groupIndex]) a.IsInProgress = false;
        }
        _groupIndex++;
        _botDue.Clear();
        _phaseStartedAt = now;

        if (_groupIndex >= s.Actions.Count)
        {
            s.Timer.Phase = "FINALIZATION";
            _phaseDeadline = now + FinalizationLength;
            Out.Log("Minden pick kész. 20 mp a játék indulásáig (csere, spell, rúna még állítható).", ConsoleColor.Yellow);
            if (_rng.NextDouble() < 0.5) BotRequest(s.Trades, "champion cserét");
        }
        else
        {
            s.Timer.Phase = "BAN_PICK";
            _phaseDeadline = now + BanPickLength;
            var group = s.Actions[_groupIndex];
            foreach (var a in group)
            {
                a.IsInProgress = true;
                if (a.ActorCellId != s.LocalPlayerCellId)
                    _botDue[a.Id] = now + TimeSpan.FromSeconds(2 + _rng.NextDouble() * 6);
            }
            var mine = group.FirstOrDefault(a => a.ActorCellId == s.LocalPlayerCellId);
            if (mine is not null)
                Out.Log($"A JÁTÉKOS JÖN: {(mine.Type == "ban" ? "BAN" : "PICK")} (30 mp, döntés a társprogramban)", ConsoleColor.Green);
        }
        PublishSession();
    }

    void TickChampSelect(DateTime now)
    {
        var s = _session;
        if (s is null) return;

        var changed = ProcessSwapResponses(now);

        if (s.Timer.Phase == "FINALIZATION")
        {
            if (now >= _phaseDeadline)
            {
                Out.Log("Champ select vége, a játék elindult. Új meccs: [M]", ConsoleColor.Yellow);
                ClearSession();
                SetPhase("InProgress");
                return;
            }
            if (changed) PublishSession();
            return;
        }

        foreach (var a in s.Actions[_groupIndex])
        {
            if (a.Completed || !_botDue.TryGetValue(a.Id, out var due) || now < due) continue;
            var options = AllChampionIds.Where(c => IsAvailable(c, a.Type)).ToArray();
            a.ChampionId = options[_rng.Next(options.Length)];
            Complete(a);
            var who = a.IsAllyAction ? "Csapattárs" : "Ellenfél";
            var position = PositionName(FindMember(a.ActorCellId)!.AssignedPosition);
            Out.Log($"{who} ({position}) {(a.Type == "ban" ? "bannolta" : "pickelte")}: {Name(a.ChampionId)}", ConsoleColor.DarkGray);
            changed = true;
        }

        if (CheckGroupDone(now)) return;

        if (now >= _phaseDeadline)
        {
            var mine = s.Actions[_groupIndex].FirstOrDefault(a => a.ActorCellId == s.LocalPlayerCellId && !a.Completed);
            if (mine is null)
            {
                AdvanceGroup(now);
                return;
            }
            if (mine.Type == "ban")
            {
                Out.Log("Lejárt az idő, a játékos nem bannolt senkit.", ConsoleColor.DarkYellow);
                mine.ChampionId = 0;
                mine.Completed = true;
                mine.IsInProgress = false;
                AdvanceGroup(now);
                return;
            }
            Out.Log("Lejárt az idő, a játékos nem lockolt: DODGE (az igazi kliensben ez büntetéssel jár).", ConsoleColor.Red);
            ClearSession();
            SetPhase("Lobby");
            return;
        }

        if (changed) PublishSession();
    }

    bool CheckGroupDone(DateTime now)
    {
        if (_session is null || _groupIndex >= _session.Actions.Count) return false;
        if (!_session.Actions[_groupIndex].All(a => a.Completed)) return false;
        AdvanceGroup(now);
        return true;
    }

    void Complete(ChampSelectAction a)
    {
        var s = _session!;
        a.Completed = true;
        a.IsInProgress = false;
        if (a.Type == "ban")
        {
            (a.IsAllyAction ? s.Bans.MyTeamBans : s.Bans.TheirTeamBans).Add(a.ChampionId);
        }
        else
        {
            var member = FindMember(a.ActorCellId)!;
            member.ChampionId = a.ChampionId;
            member.ChampionPickIntent = 0;
        }
    }

    bool IsAvailable(int champ, string type)
    {
        if (!Champions.ContainsKey(champ)) return false;
        var done = _session!.Actions.SelectMany(g => g).Where(a => a.Completed).ToList();
        if (done.Any(a => a.Type == "ban" && a.ChampionId == champ)) return false;
        if (type == "pick" && done.Any(a => a.Type == "pick" && a.ChampionId == champ)) return false;
        return true;
    }

    ChampSelectAction? PickAction(int cellId) =>
        _session!.Actions.SelectMany(g => g).FirstOrDefault(a => a.Type == "pick" && a.ActorCellId == cellId);

    bool HasPicked(int cellId) => PickAction(cellId) is { Completed: true };

    bool CanSwapPickOrder(int cellId) => PickAction(cellId) is { Completed: false, IsInProgress: false };

    void RefreshSwapStates()
    {
        var s = _session;
        if (s is null) return;
        var me = s.LocalPlayerCellId;

        foreach (var t in s.Trades)
        {
            var valid = HasPicked(me) && HasPicked(t.CellId);
            if (!valid)
            {
                t.State = "INVALID";
                _swapResponses.Remove(t.Id);
            }
            else if (t.State is not ("SENT" or "RECEIVED"))
            {
                t.State = "AVAILABLE";
            }
        }

        foreach (var p in s.PickOrderSwaps)
        {
            var valid = s.Timer.Phase == "BAN_PICK" && CanSwapPickOrder(me) && CanSwapPickOrder(p.CellId);
            if (!valid)
            {
                p.State = "INVALID";
                _swapResponses.Remove(p.Id);
            }
            else if (p.State is not ("SENT" or "RECEIVED"))
            {
                p.State = "AVAILABLE";
            }
        }
    }

    void BotRequest(List<SwapEntry> list, string what)
    {
        RefreshSwapStates();
        var options = list.Where(e => e.State == "AVAILABLE").ToList();
        if (options.Count == 0) return;
        var entry = options[_rng.Next(options.Count)];
        entry.State = "RECEIVED";
        Out.Log($"Csapattárs ({PositionName(FindMember(entry.CellId)!.AssignedPosition)}) {what} kér a játékostól.", ConsoleColor.Yellow);
    }

    bool ProcessSwapResponses(DateTime now)
    {
        RefreshSwapStates();
        var changed = false;
        foreach (var (id, due) in _swapResponses.ToList())
        {
            if (now < due) continue;
            _swapResponses.Remove(id);
            ResolveSentRequest(id);
            changed = true;
        }
        return changed;
    }

    void ResolveSentRequest(int id)
    {
        var s = _session!;
        var trade = s.Trades.FirstOrDefault(e => e.Id == id);
        var isTrade = trade is not null;
        var entry = trade ?? s.PickOrderSwaps.FirstOrDefault(e => e.Id == id);
        if (entry is null || entry.State != "SENT") return;

        var who = $"Csapattárs ({PositionName(FindMember(entry.CellId)!.AssignedPosition)})";
        entry.State = "AVAILABLE";
        if (_rng.NextDouble() < 0.6)
        {
            Out.Log($"{who} ELFOGADTA a cserét.", ConsoleColor.Cyan);
            if (isTrade) ExecuteTrade(entry.CellId); else ExecutePickOrderSwap(entry.CellId);
        }
        else
        {
            Out.Log($"{who} elutasította a cserét.", ConsoleColor.DarkYellow);
        }
    }

    void ExecuteTrade(int otherCellId)
    {
        var s = _session!;
        var me = FindMember(s.LocalPlayerCellId)!;
        var other = FindMember(otherCellId)!;
        var myPick = PickAction(me.CellId)!;
        var theirPick = PickAction(otherCellId)!;
        (me.ChampionId, other.ChampionId) = (other.ChampionId, me.ChampionId);
        (myPick.ChampionId, theirPick.ChampionId) = (theirPick.ChampionId, myPick.ChampionId);
        Out.Log($"Champion csere megtörtént, a játékos championja most: {Name(me.ChampionId)}", ConsoleColor.Cyan);
    }

    void ExecutePickOrderSwap(int otherCellId)
    {
        var s = _session!;
        var mine = PickAction(s.LocalPlayerCellId)!;
        var theirs = PickAction(otherCellId)!;
        (mine.ActorCellId, theirs.ActorCellId) = (theirs.ActorCellId, mine.ActorCellId);
        Out.Log("Pick sorrend csere megtörtént.", ConsoleColor.Cyan);
    }

    TeamMember? FindMember(int cellId) =>
        _session!.MyTeam.Concat(_session.TheirTeam).FirstOrDefault(m => m.CellId == cellId);

    void UpdateTimer()
    {
        var s = _session!;
        var now = DateTime.UtcNow;
        s.Timer.TotalTimeInPhase = (long)(_phaseDeadline - _phaseStartedAt).TotalMilliseconds;
        s.Timer.AdjustedTimeLeftInPhase = Math.Max(0, (long)(_phaseDeadline - now).TotalMilliseconds);
        s.Timer.InternalNowInEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    void SetPhase(string phase)
    {
        if (_phase == phase) return;
        _phase = phase;
        _hub.Publish(PhaseUri, "Update", phase);
    }

    void PublishGameflow() => _hub.Publish("/lol-gameflow/v1/session", "Update", GameflowSession());

    void PublishReadyCheck(string eventType)
    {
        if (_readyCheck is null) return;
        _readyCheck.Timer = (DateTime.UtcNow - _readyCheckStartedAt).TotalSeconds;
        _hub.Publish(ReadyCheckUri, eventType, _readyCheck);
    }

    void PublishSession()
    {
        if (_session is null) return;
        UpdateTimer();
        RefreshSwapStates();
        _hub.Publish(SessionUri, "Update", _session);
    }

    void ClearReadyCheck()
    {
        if (_readyCheck is not null) _hub.Publish(ReadyCheckUri, "Delete", null);
        _readyCheck = null;
        _acceptedAt = null;
    }

    void ClearSession()
    {
        if (_session is not null) _hub.Publish(SessionUri, "Delete", null);
        _session = null;
        _botDue.Clear();
        _swapResponses.Clear();
    }
}
