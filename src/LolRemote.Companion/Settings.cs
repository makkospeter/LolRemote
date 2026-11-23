using System.Text.Json;

namespace LolRemote.Companion;

public static class AppPaths
{
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LolRemote");

    public static string PathFor(string fileName)
    {
        Directory.CreateDirectory(Folder);
        return Path.Combine(Folder, fileName);
    }
}

public sealed class Settings
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly object Sync = new();

    public bool AutoAccept { get; set; }
    public bool AutoApplyTemplate { get; set; } = true;

    public static Settings Load()
    {
        try
        {
            var path = AppPaths.PathFor("settings.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options) ?? new Settings();
        }
        catch (Exception)
        {
        }
        return new Settings();
    }

    public void Save()
    {
        lock (Sync)
        {
            try
            {
                File.WriteAllText(AppPaths.PathFor("settings.json"), JsonSerializer.Serialize(this, Options));
            }
            catch (Exception)
            {
            }
        }
    }
}

public sealed class RuneTemplate
{
    public int ChampionId { get; set; }
    public string ChampionName { get; set; } = "";
    public string RuneName { get; set; } = "";
    public int PrimaryStyleId { get; set; }
    public int SubStyleId { get; set; }
    public List<int> SelectedPerkIds { get; set; } = new();
    public long Spell1Id { get; set; }
    public long Spell2Id { get; set; }
    public DateTime SavedAt { get; set; }
}

public sealed class TemplateStore
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly object _lock = new();
    readonly Dictionary<int, RuneTemplate> _items;

    public TemplateStore()
    {
        _items = Load();
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public RuneTemplate? Get(int championId)
    {
        lock (_lock) return _items.TryGetValue(championId, out var template) ? template : null;
    }

    public void Set(RuneTemplate template)
    {
        lock (_lock)
        {
            _items[template.ChampionId] = template;
            Save();
        }
    }

    public bool Remove(int championId)
    {
        lock (_lock)
        {
            var removed = _items.Remove(championId);
            if (removed) Save();
            return removed;
        }
    }

    static Dictionary<int, RuneTemplate> Load()
    {
        try
        {
            var path = AppPaths.PathFor("templates.json");
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<RuneTemplate>>(File.ReadAllText(path), Options) ?? new List<RuneTemplate>();
                return list.Where(t => t.ChampionId != 0).GroupBy(t => t.ChampionId).ToDictionary(g => g.Key, g => g.Last());
            }
        }
        catch (Exception)
        {
        }
        return new Dictionary<int, RuneTemplate>();
    }

    void Save()
    {
        try
        {
            var list = _items.Values.OrderBy(t => t.ChampionName).ToList();
            File.WriteAllText(AppPaths.PathFor("templates.json"), JsonSerializer.Serialize(list, Options));
        }
        catch (Exception)
        {
        }
    }
}
