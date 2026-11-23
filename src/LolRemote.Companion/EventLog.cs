namespace LolRemote.Companion;

public record LogEntry(string Time, string Text, ConsoleColor Color);

public sealed class EventLog
{
    readonly List<LogEntry> _entries = new();
    readonly object _lock = new();
    string _lastText = "";

    static readonly object FileLock = new();

    public static string? FilePath { get; set; }

    public static void WriteFile(string text)
    {
        var path = FilePath;
        if (path is null) return;
        try
        {
            lock (FileLock)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_000_000) info.Delete();
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }

    public event Action? Changed;

    public void Add(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (_lock)
        {
            if (text == _lastText) return;
            _lastText = text;
            _entries.Add(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), text, color));
            if (_entries.Count > 100) _entries.RemoveAt(0);
        }
        WriteFile(text);
        Changed?.Invoke();
    }

    public List<LogEntry> Last(int count)
    {
        lock (_lock) return _entries.TakeLast(count).ToList();
    }
}
