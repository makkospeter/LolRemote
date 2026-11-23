namespace LolRemote.Companion;

public sealed class ConsoleUi
{
    enum Mode { Normal, ChampSearch, SpellPick, RunePick, SwapPick }

    const int Width = 64;
    const int MaxMatches = 6;

    readonly EventLog _log;
    readonly string _sourceName;
    readonly bool _isMock;
    readonly List<(string Text, ConsoleColor Color)> _lines = new();

    volatile ChampSelectController? _controller;
    volatile bool _dirty = true;
    Mode _mode = Mode.Normal;
    string _search = "";
    List<(int Id, string Name)> _champOptions = new();
    long? _firstSpell;
    DateTime _lastRender;
    int _previousLineCount;
    int _lastWidth;
    int _lastHeight;

    public ConsoleUi(EventLog log, string sourceName, bool isMock)
    {
        _log = log;
        _sourceName = sourceName;
        _isMock = isMock;
        _log.Changed += MarkDirty;
    }

    public Func<string>? PhoneInfo { get; set; }
    public PhoneServer? Phone { get; set; }
    bool? _qrOverride;

    bool ShowQr => Phone is { Running: true } && (_qrOverride ?? Phone.ClientCount == 0);

    public ChampSelectController? Controller
    {
        get => _controller;
        set
        {
            _controller = value;
            _mode = Mode.Normal;
            _dirty = true;
        }
    }

    public void MarkDirty() => _dirty = true;

    public async Task RunAsync(CancellationTokenSource cts)
    {
        Console.CursorVisible = false;
        Console.Clear();
        while (!cts.IsCancellationRequested)
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                try
                {
                    await HandleKeyAsync(key, cts);
                }
                catch (Exception ex)
                {
                    _log.Add($"Hiba: {ex.Message}", ConsoleColor.Red);
                }
                _dirty = true;
            }

            if (_controller is { Session: null } && _mode is Mode.RunePick or Mode.SwapPick or Mode.SpellPick or Mode.ChampSearch)
                _mode = Mode.Normal;

            if (_dirty || DateTime.UtcNow - _lastRender > TimeSpan.FromSeconds(1))
            {
                _dirty = false;
                _lastRender = DateTime.UtcNow;
                Render();
            }
            await Task.Delay(50);
        }
        Console.ResetColor();
        Console.CursorVisible = true;
    }

    void Render()
    {
        var width = Math.Max(20, Console.WindowWidth - 1);
        var height = Math.Max(12, Console.WindowHeight);
        if (width != _lastWidth || height != _lastHeight)
        {
            _lastWidth = width;
            _lastHeight = height;
            Console.Clear();
            _previousLineCount = 0;
        }

        _lines.Clear();
        BuildScreen(height);

        var count = Math.Min(_lines.Count, height - 1);
        for (var i = 0; i < count; i++)
        {
            var (text, color) = _lines[i];
            Console.SetCursorPosition(0, i);
            Console.ForegroundColor = color;
            Console.Write(text.Length > width ? text[..width] : text.PadRight(width));
        }
        Console.ResetColor();
        for (var i = count; i < Math.Min(_previousLineCount, height - 1); i++)
        {
            Console.SetCursorPosition(0, i);
            Console.Write(new string(' ', width));
        }
        _previousLineCount = count;

        if (OperatingSystem.IsWindows())
        {
            try { Console.SetWindowPosition(0, 0); } catch (Exception) { }
        }
    }

    void Line(string text = "", ConsoleColor color = ConsoleColor.Gray) => _lines.Add((text, color));

    List<(string Text, ConsoleColor Color)> Capture(Action build)
    {
        var start = _lines.Count;
        build();
        var part = _lines.GetRange(start, _lines.Count - start);
        _lines.RemoveRange(start, _lines.Count - start);
        return part;
    }

    void BuildScreen(int height)
    {
        var c = _controller;
        var footer = Capture(() => BuildFooter(c));
        var top = Capture(() => BuildTop(c, false));
        if (top.Count + footer.Count + 5 > height - 1) top = Capture(() => BuildTop(c, true));

        if (ShowQr && _mode == Mode.Normal)
        {
            var qr = Capture(BuildQr);
            if (top.Count + qr.Count + footer.Count + 3 <= height - 1) top.AddRange(qr);
            else top.Add((" (Nagyítsd meg az ablakot a QR-kódhoz, vagy nyomd meg a [B] gombot.)", ConsoleColor.Yellow));
        }

        var logLines = Math.Clamp(height - 1 - top.Count - footer.Count - 2, 1, 10);
        _lines.AddRange(top);
        Line(" ESEMÉNYEK " + new string('─', Width - 11), ConsoleColor.DarkCyan);
        var entries = _log.Last(logLines);
        foreach (var entry in entries) Line($" {entry.Time}  {entry.Text}", entry.Color);
        for (var i = entries.Count; i < logLines; i++) Line();
        Line(new string('─', Width), ConsoleColor.DarkCyan);
        _lines.AddRange(footer);
    }

    void BuildQr()
    {
        var phone = Phone!;
        Line(" TELEFON CSATLAKOZTATÁSA: olvasd be a kamerával (ugyanaz a WiFi kell)", ConsoleColor.Yellow);
        foreach (var row in phone.QrLines()) Line("   " + row, ConsoleColor.White);
        Line($" Vagy nyisd meg: {phone.BaseUrl}   kód: {phone.PairingCode}", ConsoleColor.Magenta);
        Line(" [B] részletes útmutató a böngészőben   [Q] QR-kód elrejtése", ConsoleColor.DarkGray);
    }

    void BuildTop(ChampSelectController? c, bool compact)
    {
        Line(new string('═', Width), ConsoleColor.DarkCyan);
        Line(c is null
                ? $" LOLREMOTE TÁRSPROGRAM   Kapcsolat: NINCS ({_sourceName} keresése...)"
                : $" LOLREMOTE TÁRSPROGRAM   Kapcsolat: OK ({_sourceName})",
            c is null ? ConsoleColor.Red : ConsoleColor.Green);
        if (PhoneInfo is not null) Line($" TELEFON: {PhoneInfo()}", ConsoleColor.Magenta);
        Line(new string('═', Width), ConsoleColor.DarkCyan);

        if (c is null)
        {
            Line(_isMock
                ? " Indítsd el a MOCK ablakot, a társprogram magától csatlakozik."
                : " Indítsd el a League klienst, a társprogram magától csatlakozik.", ConsoleColor.Yellow);
            return;
        }

        var seconds = c.SecondsLeft();
        var mode = c.ModeLabel is null ? "" : $" ({c.ModeLabel})";
        Line($" ÁLLAPOT: {ChampSelectController.PhaseText(c.Phase)}{mode}{(seconds is int s ? $"      Idő: {s} mp" : "")}", ConsoleColor.White);
        Line($" Auto-accept: {OnOff(c.Settings.AutoAccept)} [U]    Auto-sablon: {OnOff(c.Settings.AutoApplyTemplate)} [O]", ConsoleColor.DarkGray);

        if (c.Phase is "None" or "Lobby" or "InProgress" && _isMock)
            Line(" Tipp: a MOCK ablakában nyomj [M]-et a meccskereséshez.", ConsoleColor.DarkGray);

        if (c.Phase == "ReadyCheck" && c.ReadyCheck is { } rc)
        {
            if (rc.PlayerResponse == "None")
                Line(" >>> MEGVAN A MECCS!   [A] Elfogad   [D] Elutasít", ConsoleColor.Yellow);
            else if (rc.PlayerResponse == "Accepted")
                Line(" Elfogadva, várjuk a többieket...", ConsoleColor.Cyan);
            else
                Line(" Elutasítva.", ConsoleColor.Red);
        }

        if (c.Session is { } session) BuildChampSelect(c, session, compact);
    }

    void BuildChampSelect(ChampSelectController c, ChampSelectSession s, bool compact)
    {
        var mine = c.MyCurrentAction();
        var me = c.Me;

        if (s.Timer.Phase == "FINALIZATION")
        {
            Line(" Minden pick kész.   [T] Csere   [S] Spell   [R] Rúna", ConsoleColor.Yellow);
        }
        else if (c.NoPickAvailable)
        {
            Line(" Ebben a módban a kliens nem ad pick-lehetőséget: válassz championt a PC-n.", ConsoleColor.DarkYellow);
        }
        else if (mine is null)
        {
            Line(" Most mások választanak, várj a sorodra.", ConsoleColor.DarkGray);
        }
        else
        {
            var what = mine.Type == "ban" ? "BAN" : "PICK";
            var chosen = mine.ChampionId == 0 ? "még semmi" : c.ChampionName(mine.ChampionId);
            var keys = mine.ChampionId == 0 ? "[C] Keresés" : "[C] Másik  [L] LOCK IN";
            Line($" >>> TE JÖSSZ: {what}   Kijelölve: {chosen}   {keys}", ConsoleColor.Green);
        }

        if (c.IncomingRequest() is { } incoming)
            Line($" >>> {incoming.Label}   [Y] Elfogad  [N] Elutasít", ConsoleColor.Yellow);

        if (!compact)
        {
            Line();
            Line($" {"SAJÁT CSAPAT",-36}ELLENFÉL", ConsoleColor.White);
            for (var i = 0; i < 5; i++)
            {
                var ally = i < s.MyTeam.Count ? s.MyTeam[i] : null;
                var enemy = i < s.TheirTeam.Count ? s.TheirTeam[i] : null;
                var isMe = ally is not null && ally.CellId == s.LocalPlayerCellId;
                var left = ally is null
                    ? ""
                    : $"{(isMe ? ">" : " ")}{ChampSelectController.PositionText(ally.AssignedPosition),-8} {MemberChampion(c, ally)}{(isMe ? "  <- TE" : "")}";
                var right = enemy is null ? "" : MemberChampion(c, enemy);
                Line($" {left,-36}{right}", isMe ? ConsoleColor.Green : ConsoleColor.Gray);
            }
            Line($" Bannok: {Names(c, s.Bans?.MyTeamBans)}  |  ellenfél: {Names(c, s.Bans?.TheirTeamBans)}", ConsoleColor.DarkGray);
        }

        if (me is not null)
            Line($" Spellek: {c.SpellName(me.Spell1Id)} (D) + {c.SpellName(me.Spell2Id)} (F)", ConsoleColor.Gray);
        Line($" Rúnák: {c.RuneSummary(c.CurrentRunePage)}", ConsoleColor.Gray);

        var championId = c.MyChampionId();
        if (championId != 0)
        {
            var template = c.MyTemplate();
            Line(template is null
                ? $" Sablon ({c.ChampionName(championId)}): nincs   [K] mentés"
                : $" Sablon ({c.ChampionName(championId)}): {c.TemplateSummary(template)}   [J] alkalmaz  [K] felülír",
                template is null ? ConsoleColor.DarkGray : ConsoleColor.Cyan);
        }
    }

    static string OnOff(bool value) => value ? "BE" : "KI";

    static string MemberChampion(ChampSelectController c, TeamMember m) =>
        m.ChampionId != 0 ? c.ChampionName(m.ChampionId)
        : m.ChampionPickIntent != 0 ? $"({c.ChampionName(m.ChampionPickIntent)}?)"
        : "...";

    static string Names(ChampSelectController c, List<int>? ids)
    {
        var names = (ids ?? new List<int>()).Where(id => id != 0).Select(c.ChampionName).ToList();
        return names.Count == 0 ? "-" : string.Join(", ", names);
    }

    void BuildFooter(ChampSelectController? c)
    {
        switch (_mode)
        {
            case Mode.ChampSearch:
                Line($" CHAMPION KERESÉS: {_search}_", ConsoleColor.Yellow);
                var matches = Matches();
                if (matches.Count == 0) Line("   (nincs találat)", ConsoleColor.DarkGray);
                for (var i = 0; i < matches.Count; i++)
                    Line($"   [{i + 1}] {matches[i].Name}", i == 0 ? ConsoleColor.White : ConsoleColor.Gray);
                Line(" Gépeld a nevét | [1-6] választ | [Enter] az első | [Esc] mégse", ConsoleColor.DarkGray);
                break;

            case Mode.SpellPick:
                Line(_firstSpell is null
                    ? " SPELL CSERE: válaszd ki az ELSŐ spellt (D gomb)"
                    : $" SPELL CSERE: első: {c?.SpellName(_firstSpell.Value)}. Most a MÁSODIK (F gomb)", ConsoleColor.Yellow);
                var spells = c?.Spells ?? new List<(long Id, string Name)>();
                AddColumns(spells.Select((sp, i) => $"[{i + 1}] {sp.Name}").ToList(), 3);
                Line(" [1-9] választ | [Esc] mégse", ConsoleColor.DarkGray);
                break;

            case Mode.RunePick:
                Line(" RÚNAOLDAL VÁLASZTÁS:", ConsoleColor.Yellow);
                var pages = c?.RunePages ?? new List<RunePage>();
                if (pages.Count == 0) Line("   (nincs rúnaoldal)", ConsoleColor.DarkGray);
                for (var i = 0; i < pages.Count && i < 9; i++)
                    Line($"   [{i + 1}] {(pages[i].Current ? "* " : "  ")}{c!.RuneSummary(pages[i])}",
                        pages[i].Current ? ConsoleColor.Green : ConsoleColor.Gray);
                Line(" [1-9] választ | [Esc] mégse   (* = jelenlegi)", ConsoleColor.DarkGray);
                break;

            case Mode.SwapPick:
                Line(" CSERE A CSAPATTÁRSAKKAL:", ConsoleColor.Yellow);
                var options = c?.SwapOptions() ?? new List<SwapOption>();
                if (options.Count == 0) Line("   Most nincs elérhető csere.", ConsoleColor.DarkGray);
                for (var i = 0; i < options.Count && i < 9; i++)
                    Line($"   [{i + 1}] {options[i].Label}");
                Line(" [1-9] választ | [Esc] mégse", ConsoleColor.DarkGray);
                break;

            default:
                Line(" " + KeyHints(c), ConsoleColor.White);
                break;
        }
    }

    void AddColumns(List<string> items, int perLine)
    {
        for (var i = 0; i < items.Count; i += perLine)
            Line("   " + string.Concat(items.Skip(i).Take(perLine).Select(t => t.PadRight(18))));
    }

    static string KeyHints(ChampSelectController? c)
    {
        var hints = new List<string>();
        if (c is not null)
        {
            if (c.Phase == "ReadyCheck" && c.ReadyCheck?.PlayerResponse == "None")
            {
                hints.Add("[A] Elfogad");
                hints.Add("[D] Elutasít");
            }
            if (c.MyCurrentAction() is { } action)
            {
                hints.Add("[C] Champion");
                if (action.ChampionId != 0) hints.Add("[L] Lock");
            }
            if (c.IncomingRequest() is not null)
            {
                hints.Add("[Y] Igen");
                hints.Add("[N] Nem");
            }
            if (c.Session is not null)
            {
                hints.Add("[S] Spell");
                hints.Add("[R] Rúna");
                hints.Add("[T] Csere");
                if (c.MyChampionId() != 0) hints.Add(c.MyTemplate() is null ? "[K] Sablon" : "[J] Sablon");
            }
        }
        hints.Add("[Q] QR");
        hints.Add("[X] Kilépés");
        return string.Join("  ", hints);
    }

    List<(int Id, string Name)> Matches() =>
        _champOptions
            .Where(o => o.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Name.StartsWith(_search, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(o => o.Name)
            .Take(MaxMatches)
            .ToList();

    async Task HandleKeyAsync(ConsoleKeyInfo key, CancellationTokenSource cts)
    {
        var c = _controller;

        switch (_mode)
        {
            case Mode.ChampSearch:
                await HandleSearchKeyAsync(key, c);
                return;
            case Mode.SpellPick:
                await HandleSpellKeyAsync(key, c);
                return;
            case Mode.RunePick:
                await HandleRuneKeyAsync(key, c);
                return;
            case Mode.SwapPick:
                await HandleSwapKeyAsync(key, c);
                return;
        }

        if (key.Key == ConsoleKey.X)
        {
            cts.Cancel();
            return;
        }
        if (key.Key == ConsoleKey.Q)
        {
            _qrOverride = !ShowQr;
            if (OperatingSystem.IsWindows()) Console.Clear();
            return;
        }
        if (key.Key == ConsoleKey.B)
        {
            Phone?.OpenSetupPage();
            return;
        }
        if (c is null) return;

        switch (key.Key)
        {
            case ConsoleKey.A:
                Report(await c.AcceptAsync());
                break;
            case ConsoleKey.D:
                Report(await c.DeclineAsync());
                break;
            case ConsoleKey.C when c.MyCurrentAction() is not null:
                _champOptions = await c.AvailableChampionsAsync();
                _search = "";
                _mode = Mode.ChampSearch;
                break;
            case ConsoleKey.L:
                Report(await c.LockAsync());
                break;
            case ConsoleKey.S when c.Session is not null:
                _firstSpell = null;
                _mode = Mode.SpellPick;
                break;
            case ConsoleKey.R when c.CanChangeRunes:
                _mode = Mode.RunePick;
                break;
            case ConsoleKey.R:
                _log.Add("Rúnaoldalt csak champ selectben lehet választani.", ConsoleColor.DarkYellow);
                break;
            case ConsoleKey.T when c.Session is not null:
                _mode = Mode.SwapPick;
                break;
            case ConsoleKey.U:
                Report(c.SetSetting("autoAccept", !c.Settings.AutoAccept));
                break;
            case ConsoleKey.O:
                Report(c.SetSetting("autoApplyTemplate", !c.Settings.AutoApplyTemplate));
                break;
            case ConsoleKey.K when c.Session is not null:
                Report(c.SaveTemplate());
                break;
            case ConsoleKey.J when c.Session is not null:
                Report(await c.ApplyTemplateAsync());
                break;
            case ConsoleKey.Y when c.IncomingRequest() is { } toAccept:
                Report(await c.SwapAsync(toAccept.IsTrade, toAccept.Id, "accept"));
                break;
            case ConsoleKey.N when c.IncomingRequest() is { } toDecline:
                Report(await c.SwapAsync(toDecline.IsTrade, toDecline.Id, "decline"));
                break;
        }
    }

    async Task HandleSearchKeyAsync(ConsoleKeyInfo key, ChampSelectController? c)
    {
        if (key.Key == ConsoleKey.Escape || c is null)
        {
            _mode = Mode.Normal;
            var current = c?.MyCurrentAction();
            if (current is not null && current.ChampionId != 0)
                _log.Add($"Keresés bezárva, a kijelölés maradt: {c!.ChampionName(current.ChampionId)}", ConsoleColor.DarkGray);
            return;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (_search.Length > 0) _search = _search[..^1];
            return;
        }

        int? index = key.Key == ConsoleKey.Enter ? 0
            : key.KeyChar >= '1' && key.KeyChar <= '0' + MaxMatches ? key.KeyChar - '1'
            : (int?)null;

        if (index is int i)
        {
            var matches = Matches();
            if (i >= matches.Count) return;
            _mode = Mode.Normal;
            Report(await c.HoverAsync(matches[i].Id));
            return;
        }

        if (char.IsLetter(key.KeyChar) || key.KeyChar is ' ' or '\'' or '.' or '&')
            _search += key.KeyChar;
    }

    async Task HandleSpellKeyAsync(ConsoleKeyInfo key, ChampSelectController? c)
    {
        if (key.Key == ConsoleKey.Escape || c is null)
        {
            _mode = Mode.Normal;
            return;
        }
        if (key.KeyChar is < '1' or > '9') return;

        var i = key.KeyChar - '1';
        if (i >= c.Spells.Count) return;
        var id = c.Spells[i].Id;

        if (_firstSpell is null)
        {
            _firstSpell = id;
            return;
        }
        if (id == _firstSpell)
        {
            _log.Add("A két spell nem lehet ugyanaz.", ConsoleColor.Red);
            return;
        }
        _mode = Mode.Normal;
        Report(await c.SetSpellsAsync(_firstSpell.Value, id));
    }

    async Task HandleRuneKeyAsync(ConsoleKeyInfo key, ChampSelectController? c)
    {
        if (key.Key == ConsoleKey.Escape || c is null)
        {
            _mode = Mode.Normal;
            return;
        }
        if (key.KeyChar is < '1' or > '9') return;

        var i = key.KeyChar - '1';
        var pages = c.RunePages;
        if (i >= pages.Count) return;
        _mode = Mode.Normal;
        Report(await c.SetRunePageAsync(pages[i].Id));
    }

    async Task HandleSwapKeyAsync(ConsoleKeyInfo key, ChampSelectController? c)
    {
        if (key.Key == ConsoleKey.Escape || c is null)
        {
            _mode = Mode.Normal;
            return;
        }
        if (key.KeyChar is < '1' or > '9') return;

        var i = key.KeyChar - '1';
        var options = c.SwapOptions();
        if (i >= options.Count) return;
        _mode = Mode.Normal;
        var option = options[i];
        Report(await c.SwapAsync(option.IsTrade, option.Id, option.Verb));
    }

    void Report(string? error)
    {
        if (error is not null) _log.Add(error, ConsoleColor.Red);
    }
}
