using System.Text.Json;

namespace LolRemote.Companion;

public sealed class DebugDump
{
    static readonly string[] Prefixes =
    {
        "/lol-champ-select/v1/session",
        "/lol-gameflow/v1/",
        "/lol-matchmaking/v1/ready-check",
        "/lol-perks/v1/currentpage",
        "/lol-lobby/v2/lobby"
    };

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    readonly object _lock = new();
    readonly HashSet<string> _seen = new();
    int _count;

    public DebugDump(string folder)
    {
        Folder = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public void WriteRaw(string name, JsonElement data)
    {
        lock (_lock)
        {
            if (_count >= 300) return;
            _count++;
            var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_').ToArray());
            try
            {
                File.WriteAllText(Path.Combine(Folder, $"{_count:D3}-{DateTime.Now:HHmmss}-{safe}.json"),
                    data.ValueKind == JsonValueKind.Undefined ? "null" : JsonSerializer.Serialize(data, Options));
            }
            catch (Exception)
            {
            }
        }
    }

    public void Write(string uri, string eventType, JsonElement data)
    {
        if (!Prefixes.Any(p => uri.StartsWith(p, StringComparison.Ordinal))) return;
        lock (_lock)
        {
            var isSession = uri == "/lol-champ-select/v1/session";
            if (!isSession && !_seen.Add($"{uri}|{eventType}")) return;
            if (_count >= 300) return;
            _count++;
            var safe = new string(uri.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
            var file = Path.Combine(Folder, $"{_count:D3}-{DateTime.Now:HHmmss}-{eventType}-{safe}.json");
            try
            {
                var text = data.ValueKind == JsonValueKind.Undefined ? "null" : JsonSerializer.Serialize(data, Options);
                File.WriteAllText(file, text);
            }
            catch (Exception)
            {
            }
        }
    }
}
