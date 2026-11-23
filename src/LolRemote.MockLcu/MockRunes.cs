using System.Text.Json;

namespace LolRemote.MockLcu;

public class RunePage
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int PrimaryStyleId { get; set; }
    public int SubStyleId { get; set; }
    public List<int> SelectedPerkIds { get; set; } = new();
    public bool Current { get; set; }
    public bool IsEditable { get; set; } = true;
    public bool IsDeletable { get; set; } = true;
}

public class MockRunes
{
    public static readonly Dictionary<int, string> Styles = new()
    {
        [8000] = "Precision", [8100] = "Domination", [8200] = "Sorcery",
        [8300] = "Inspiration", [8400] = "Resolve"
    };

    public static readonly Dictionary<int, string> Perks = new()
    {
        [8008] = "Lethal Tempo", [9111] = "Triumph", [9104] = "Legend: Alacrity", [8299] = "Last Stand",
        [8112] = "Electrocute", [8139] = "Taste of Blood", [8138] = "Eyeball Collection", [8106] = "Ultimate Hunter",
        [8226] = "Manaflow Band", [8237] = "Scorch",
        [8437] = "Grasp of the Undying", [8446] = "Demolish", [8444] = "Second Wind", [8451] = "Overgrowth",
        [8453] = "Revitalize", [8304] = "Magical Footwear", [8345] = "Biscuit Delivery",
        [8010] = "Conqueror", [8229] = "Arcane Comet", [8275] = "Nimbus Cloak", [8210] = "Transcendence",
        [8005] = "Press the Attack", [9101] = "Overheal", [8017] = "Cut Down", [8014] = "Coup de Grace",
        [5008] = "Adaptive Force", [5005] = "Attack Speed", [5011] = "Health", [5013] = "Tenacity and Slow Resist"
    };

    static readonly (int Primary, int Sub, int[] Perks)[] RecommendedSets =
    {
        (8000, 8400, new[] { 8010, 9111, 9104, 8299, 8444, 8453, 5005, 5008, 5011 }),
        (8400, 8300, new[] { 8437, 8446, 8444, 8451, 8304, 8345, 5008, 5011, 5013 }),
        (8100, 8200, new[] { 8112, 8139, 8138, 8106, 8226, 8237, 5008, 5008, 5011 }),
        (8200, 8100, new[] { 8229, 8226, 8210, 8237, 8139, 8106, 5008, 5008, 5011 }),
        (8000, 8100, new[] { 8005, 9101, 9104, 8017, 8139, 8106, 5005, 5008, 5011 }),
        (8000, 8200, new[] { 8008, 9111, 9104, 8014, 8226, 8275, 5005, 5008, 5011 })
    };

    public object Recommended(int championId, string position)
    {
        var positions = new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" };
        var offset = Math.Max(0, Array.IndexOf(positions, position)) + championId;
        return Enumerable.Range(0, 3).Select(i =>
        {
            var set = RecommendedSets[(offset + i) % RecommendedSets.Length];
            return new
            {
                position,
                isDefaultPosition = i == 0,
                primaryPerkStyleId = set.Primary,
                secondaryPerkStyleId = set.Sub,
                keystone = new { id = set.Perks[0], name = Perks.GetValueOrDefault(set.Perks[0], $"#{set.Perks[0]}") },
                perks = set.Perks.Select(id => new { id, name = Perks.GetValueOrDefault(id, $"#{id}") }).ToList()
            };
        }).ToList();
    }

    const string CurrentPageUri = "/lol-perks/v1/currentpage";
    const string PagesUri = "/lol-perks/v1/pages";

    readonly object _lock = new();
    readonly WebSocketHub _hub;
    readonly List<RunePage> _pages = new()
    {
        new RunePage
        {
            Id = 101, Name = "Mágus - Electrocute", PrimaryStyleId = 8100, SubStyleId = 8200, Current = true,
            SelectedPerkIds = new() { 8112, 8139, 8138, 8106, 8226, 8237, 5008, 5008, 5011 }
        },
        new RunePage
        {
            Id = 102, Name = "ADC - Lethal Tempo", PrimaryStyleId = 8000, SubStyleId = 8400,
            SelectedPerkIds = new() { 8008, 9111, 9104, 8299, 8444, 8453, 5005, 5008, 5011 }
        },
        new RunePage
        {
            Id = 103, Name = "Tank - Grasp", PrimaryStyleId = 8400, SubStyleId = 8300,
            SelectedPerkIds = new() { 8437, 8446, 8444, 8451, 8304, 8345, 5008, 5011, 5013 }
        }
    };

    const int MaxPages = 4;
    long _nextId = 200;

    public MockRunes(WebSocketHub hub) => _hub = hub;

    public (string? Error, string? Json) Create(RunePage body)
    {
        lock (_lock)
        {
            if (_pages.Count >= MaxPages) return ("Max pages reached", null);
            if (body.SelectedPerkIds.Count != 9) return ("Invalid page: 9 perk kell", null);
            var page = new RunePage
            {
                Id = _nextId++,
                Name = string.IsNullOrWhiteSpace(body.Name) ? "Új oldal" : body.Name,
                PrimaryStyleId = body.PrimaryStyleId,
                SubStyleId = body.SubStyleId,
                SelectedPerkIds = body.SelectedPerkIds.ToList()
            };
            _pages.Add(page);
            if (body.Current) foreach (var p in _pages) p.Current = p.Id == page.Id;
            Out.Log($"Új rúnaoldal létrehozva: {page.Name}", ConsoleColor.Cyan);
            PublishAll();
            return (null, JsonSerializer.Serialize(page, MockEngine.JsonOptions));
        }
    }

    public string? Update(long id, RunePage body)
    {
        lock (_lock)
        {
            var page = _pages.FirstOrDefault(p => p.Id == id);
            if (page is null) return "Nincs ilyen rúnaoldal.";
            if (!page.IsEditable) return "Ez a rúnaoldal nem szerkeszthető.";
            if (body.SelectedPerkIds.Count != 9) return "Invalid page: 9 perk kell";
            page.Name = body.Name;
            page.PrimaryStyleId = body.PrimaryStyleId;
            page.SubStyleId = body.SubStyleId;
            page.SelectedPerkIds = body.SelectedPerkIds.ToList();
            if (body.Current) foreach (var p in _pages) p.Current = p.Id == page.Id;
            Out.Log($"Rúnaoldal frissítve: {page.Name}", ConsoleColor.Cyan);
            PublishAll();
            return null;
        }
    }

    void PublishAll()
    {
        if (_pages.FirstOrDefault(p => p.Current) is { } current) _hub.Publish(CurrentPageUri, "Update", current);
        _hub.Publish(PagesUri, "Update", _pages);
    }

    public string PagesJson()
    {
        lock (_lock) return JsonSerializer.Serialize(_pages, MockEngine.JsonOptions);
    }

    public string? CurrentJson()
    {
        lock (_lock)
        {
            var current = _pages.FirstOrDefault(p => p.Current);
            return current is null ? null : JsonSerializer.Serialize(current, MockEngine.JsonOptions);
        }
    }

    public string? SetCurrent(long pageId)
    {
        lock (_lock)
        {
            var page = _pages.FirstOrDefault(p => p.Id == pageId);
            if (page is null) return "Nincs ilyen rúnaoldal.";
            foreach (var p in _pages) p.Current = p.Id == pageId;
            _hub.Publish(CurrentPageUri, "Update", page);
            _hub.Publish(PagesUri, "Update", _pages);
            Out.Log($"A játékos rúnaoldala: {page.Name}", ConsoleColor.Cyan);
            return null;
        }
    }
}
