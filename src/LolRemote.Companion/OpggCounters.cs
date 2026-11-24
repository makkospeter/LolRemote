using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LolRemote.Companion;

public record MatchupStat(int ChampionId, string Name, int Games, int Wins)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
}

public record SpellStat(List<int> Ids, int Games, int Wins);

public record RuneStat(int PrimaryStyleId, int SubStyleId, List<int> PerkIds, int Games, int Wins);

public record ChampionInfo(string Key, List<string> Positions);

public record MetaStat(string Name, int Tier, int Rank, double WinRate, double PickRate, double BanRate, int Games, bool IsRip);

public record MatchupData(
    string Position,
    List<MatchupStat> Counters,
    List<SpellStat> Spells,
    List<RuneStat> Runes,
    string? Tip,
    string? LaneAdvantage,
    string? PlayStyle);

public sealed class OpggCounters : IDisposable
{
    const string Endpoint = "https://mcp-api.op.gg/mcp";
    static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(30);

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly ConcurrentDictionary<string, (DateTime At, object Value)> _cache = new();
    readonly ConcurrentDictionary<int, ChampionInfo> _info = new();
    McpClient? _client;

    public Action<string, JsonElement>? DebugWriter { get; set; }

    public static string? ToOpggPosition(string assignedPosition) => assignedPosition.ToLowerInvariant() switch
    {
        "top" => "top",
        "jungle" => "jungle",
        "middle" or "mid" => "mid",
        "bottom" or "adc" => "adc",
        "utility" or "support" => "support",
        _ => null
    };

    public static string PositionText(string position) => position switch
    {
        "top" => "top",
        "jungle" => "jungle",
        "mid" => "mid",
        "adc" => "bot",
        "support" => "support",
        _ => position
    };

    public async Task<ChampionInfo> InfoAsync(int championId, string name, string? alias, CancellationToken ct)
    {
        if (_info.TryGetValue(championId, out var known)) return known;
        string? lastError = null;
        foreach (var key in KeyCandidates(name, alias))
        {
            var args = new Dictionary<string, object>
            {
                ["game_mode"] = "ranked",
                ["champion"] = key,
                ["position"] = "mid",
                ["lang"] = "en_US",
                ["desired_output_fields"] = new[] { "data.summary.positions[].name" }
            };
            JsonElement result;
            try
            {
                result = await CallAsync("lol_get_champion_analysis", args, ct);
            }
            catch (Exception ex) when (ex is McpException or HttpRequestException)
            {
                lastError = ex.Message;
                continue;
            }
            DebugWriter?.Invoke($"opgg-info-{key}", result);
            var text = TextOf(result);
            var isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
            if (isError || !text.Contains("LolGetChampionAnalysis(", StringComparison.Ordinal))
            {
                lastError = text.Length > 160 ? text[..160] : text;
                continue;
            }
            var positions = Regex.Matches(text, "Position\\(\"([A-Z]+)\"")
                .Select(m => ToOpggPosition(m.Groups[1].Value))
                .Where(p => p is not null)
                .Select(p => p!)
                .Distinct()
                .ToList();
            var info = new ChampionInfo(key, positions);
            _info[championId] = info;
            return info;
        }
        throw new McpException($"Az OP.GG nem ismeri: {name} ({lastError})");
    }

    public async Task<MatchupData> MatchupAsync(int myId, string myName, string? myAlias, int opponentId, string opponentName, string? opponentAlias, string? position, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var me = await InfoAsync(myId, myName, myAlias, ct);
            var opponent = opponentId == myId ? me : await InfoAsync(opponentId, opponentName, opponentAlias, ct);
            var pos = position ?? me.Positions.FirstOrDefault() ?? "mid";
            var cacheKey = $"mu|{myId}|{opponentId}|{pos}";
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheTime)
                return (MatchupData)cached.Value;

            var args = new Dictionary<string, object>
            {
                ["position"] = pos,
                ["my_champion"] = me.Key,
                ["opponent_champion"] = opponent.Key,
                ["lang"] = "en_US"
            };
            var result = await CallAsync("lol_get_lane_matchup_guide", args, ct);
            DebugWriter?.Invoke($"opgg-matchup-{me.Key}-{opponent.Key}-{pos}", result);
            var text = TextOf(result);
            var start = text.IndexOf('{');
            if (start < 0) throw new McpException("Az OP.GG nem adott matchup adatot.");
            using var doc = JsonDocument.Parse(text[start..(text.LastIndexOf('}') + 1)]);
            if (!doc.RootElement.TryGetProperty("data", out var data)) throw new McpException("Az OP.GG nem adott matchup adatot.");

            var counters = Array(data, "counters")
                .Select(c => new MatchupStat(Int(c, "champion_id"), Str(c, "champion_name") ?? "", Int(c, "play"), Int(c, "win")))
                .Where(c => c.ChampionId > 0 && c.Games > 0)
                .ToList();
            var spells = Array(data, "summoner_spells")
                .Select(s => new SpellStat(Ints(s, "ids"), Int(s, "play"), Int(s, "win")))
                .Where(s => s.Ids.Count == 2 && s.Games > 0)
                .ToList();
            var runes = Array(data, "runes")
                .Select(ParseRune)
                .Where(r => r is not null)
                .Select(r => r!)
                .ToList();

            var matchup = new MatchupData(pos, counters, spells, runes,
                Str(data, "opponent_champion_tip"), Str(data, "lane_advantage_champion"), Str(data, "recommended_play_style"));
            _cache[cacheKey] = (DateTime.UtcNow, matchup);
            return matchup;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MatchupData> GeneralBuildAsync(int championId, string name, string? alias, string? position, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var info = await InfoAsync(championId, name, alias, ct);
            var pos = position ?? info.Positions.FirstOrDefault() ?? "mid";
            var cacheKey = $"build|{championId}|{pos}";
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheTime)
                return (MatchupData)cached.Value;

            var args = new Dictionary<string, object>
            {
                ["game_mode"] = "ranked",
                ["champion"] = info.Key,
                ["position"] = pos,
                ["lang"] = "en_US",
                ["desired_output_fields"] = new[]
                {
                    "data.runes.{id,pick_rate,play,primary_page_id,primary_rune_ids[],secondary_page_id,secondary_rune_ids[],stat_mod_ids[],win}",
                    "data.summoner_spells.{ids[],pick_rate,play,win}"
                }
            };
            var result = await CallAsync("lol_get_champion_analysis", args, ct);
            DebugWriter?.Invoke($"opgg-build-{info.Key}-{pos}", result);
            var text = TextOf(result);

            var spells = new List<SpellStat>();
            var spellMatch = Regex.Match(text, @"SummonerSpells\(\[([\d,]+)\],(?:\[[^\]]*\],)?(\d+),(\d+)");
            var spellClass = Regex.Match(text, @"class SummonerSpells:\s*([\w,]+)");
            if (spellMatch.Success)
            {
                var ids = spellMatch.Groups[1].Value.Split(',').Select(int.Parse).ToList();
                var a = int.Parse(spellMatch.Groups[2].Value);
                var b = int.Parse(spellMatch.Groups[3].Value);
                var winFirst = spellClass.Success && spellClass.Groups[1].Value.IndexOf("win", StringComparison.Ordinal) < spellClass.Groups[1].Value.IndexOf("play", StringComparison.Ordinal);
                var (wins, games) = winFirst ? (a, b) : (b, a);
                if (ids.Count == 2) spells.Add(new SpellStat(ids, games, wins));
            }

            var runes = new List<RuneStat>();
            var runeMatch = Regex.Match(text, @"Runes\((\d+),(\d+),\[([\d,]+)\],(\d+),\[([\d,]+)\],\[([\d,]+)\],(\d+),(\d+)");
            if (runeMatch.Success)
            {
                var g = runeMatch.Groups;
                var perks = g[3].Value.Split(',').Concat(g[5].Value.Split(',')).Concat(g[6].Value.Split(',')).Select(int.Parse).ToList();
                runes.Add(new RuneStat(int.Parse(g[2].Value), int.Parse(g[4].Value), perks, int.Parse(g[7].Value), int.Parse(g[8].Value)));
            }

            var build = new MatchupData(pos, new List<MatchupStat>(), spells, runes, null, null, null);
            _cache[cacheKey] = (DateTime.UtcNow, build);
            return build;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<MetaStat>> LaneMetaAsync(string position, CancellationToken ct)
    {
        var cacheKey = $"meta|{position}";
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheTime)
            return (List<MetaStat>)cached.Value;
        await _gate.WaitAsync(ct);
        try
        {
            var className = char.ToUpperInvariant(position[0]) + position[1..];
            var args = new Dictionary<string, object>
            {
                ["position"] = position,
                ["lang"] = "en_US",
                ["desired_output_fields"] = new[] { $"data.positions.{position}[].{{ban_rate,champion,is_rip,pick_rate,play,rank,tier,win_rate}}" }
            };
            var result = await CallAsync("lol_list_lane_meta_champions", args, ct);
            DebugWriter?.Invoke($"opgg-meta-{position}", result);
            var list = ParseMeta(TextOf(result), className);
            if (list.Count == 0) throw new McpException("Az OP.GG nem adott meta adatot.");
            _cache[cacheKey] = (DateTime.UtcNow, list);
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static List<MetaStat> ParseMeta(string text, string className)
    {
        var result = new List<MetaStat>();
        var header = Regex.Match(text, $@"class {className}:\s*([\w,]+)");
        if (!header.Success) return result;
        var fields = header.Groups[1].Value.Split(',').Select(f => f.Trim()).ToList();
        foreach (Match m in Regex.Matches(text, $@"\b{className}\(((?:""(?:[^""\\]|\\.)*""|[^()""])*)\)"))
        {
            var values = Regex.Matches(m.Groups[1].Value, @"""(?:[^""\\]|\\.)*""|[^,]+")
                .Select(v => v.Value.Trim())
                .ToList();
            if (values.Count != fields.Count) continue;
            string Get(string name)
            {
                var i = fields.IndexOf(name);
                return i < 0 ? "" : values[i];
            }
            double D(string name) => double.TryParse(Get(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            var name = Get("champion").Trim('"');
            name = Regex.Unescape(name);
            if (name.Length == 0) continue;
            result.Add(new MetaStat(name, (int)D("tier"), (int)D("rank"), D("win_rate"), D("pick_rate"), D("ban_rate"), (int)D("play"), Get("is_rip") == "true"));
        }
        return result;
    }

    static RuneStat? ParseRune(JsonElement r)
    {
        var primary = Ints(r, "primary_rune_ids");
        var secondary = Ints(r, "secondary_rune_ids");
        var shards = Ints(r, "stat_mod_ids");
        if (primary.Count != 4 || secondary.Count != 2 || shards.Count != 3) return null;
        return new RuneStat(Int(r, "primary_page_id"), Int(r, "secondary_page_id"),
            primary.Concat(secondary).Concat(shards).ToList(), Int(r, "play"), Int(r, "win"));
    }

    static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static List<int> Ints(JsonElement e, string name) =>
        Array(e, name).Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToList();

    async Task<JsonElement> CallAsync(string tool, object args, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (_client is null)
                {
                    var client = new McpClient(Endpoint);
                    await client.InitializeAsync(ct);
                    _client = client;
                }
                return await _client.CallToolAsync(tool, args, ct);
            }
            catch (Exception) when (attempt == 0 && !ct.IsCancellationRequested)
            {
                _client?.Dispose();
                _client = null;
            }
        }
        throw new McpException("Az OP.GG nem érhető el.");
    }

    static string TextOf(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", content.EnumerateArray()
            .Where(c => c.TryGetProperty("text", out _))
            .Select(c => c.GetProperty("text").GetString()));
    }

    static IEnumerable<string> KeyCandidates(string name, string? alias)
    {
        var seen = new HashSet<string>();
        IEnumerable<string> All()
        {
            var letters = Regex.Replace(name, "[^A-Za-z ]", "");
            if (!string.IsNullOrWhiteSpace(alias))
            {
                yield return Regex.Replace(alias, "(?<=[a-z])(?=[A-Z])", "_").ToUpperInvariant();
                yield return alias.ToUpperInvariant();
            }
            yield return Regex.Replace(letters.Trim(), "\\s+", "_").ToUpperInvariant();
            yield return letters.Replace(" ", "").ToUpperInvariant();
        }
        foreach (var candidate in All())
            if (candidate.Length > 0 && seen.Add(candidate)) yield return candidate;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _gate.Dispose();
    }
}
