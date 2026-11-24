using System.Text;

namespace LolRemote.Companion;

public static class LcuHelp
{
    public static async Task<int> DumpAsync(string outFile)
    {
        var path = LockfileInfo.DetectPath();
        var info = path is null ? null : LockfileInfo.TryRead(path);
        if (info is null)
        {
            await File.WriteAllTextAsync(outFile, "NINCS LOCKFILE - fut a League kliens?");
            return 1;
        }
        await using var lcu = new LcuConnection(info);
        var sb = new StringBuilder();
        foreach (var query in new[] { "/help", "/help?format=Full" })
        {
            try
            {
                var bytes = await lcu.GetBytesAsync(query);
                var text = bytes is null ? "(null)" : Encoding.UTF8.GetString(bytes);
                await File.WriteAllTextAsync(outFile + (query.Contains("Full") ? ".full.json" : ".json"), text);
                foreach (var line in text.Split(new[] { ',', '\n' }))
                    if (line.Contains("champ-select", StringComparison.OrdinalIgnoreCase) || line.Contains("ChampSelect", StringComparison.Ordinal))
                        sb.AppendLine(line.Trim());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{query}: HIBA {ex.Message}");
            }
        }
        await File.WriteAllTextAsync(outFile, sb.ToString());
        return 0;
    }
}
