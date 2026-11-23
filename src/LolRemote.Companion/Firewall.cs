using System.ComponentModel;
using System.Diagnostics;

namespace LolRemote.Companion;

public static class Firewall
{
    static readonly object Sync = new();
    static (DateTime At, string Status) _cache = (DateTime.MinValue, "unknown");

    public static string Status(bool fresh = false)
    {
        lock (Sync)
        {
            if (!fresh && DateTime.UtcNow - _cache.At < TimeSpan.FromSeconds(10)) return _cache.Status;
            _cache = (DateTime.UtcNow, Check());
            return _cache.Status;
        }
    }

    static string Check()
    {
        if (!OperatingSystem.IsWindows()) return "ok";
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return "unknown";
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return "unknown";
            dynamic policy = Activator.CreateInstance(type)!;
            var allow = false;
            var block = false;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (string.IsNullOrEmpty(app)) continue;
                string full;
                try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(app)); }
                catch (Exception) { continue; }
                if (!string.Equals(full, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if ((int)rule.Direction != 1 || !(bool)rule.Enabled) continue;
                if ((int)rule.Action == 1) allow = true;
                else block = true;
            }
            return block ? "blocked" : allow ? "ok" : "missing";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    public static string? Fix()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return "Nem található a program elérési útja.";
        var arguments =
            $"/c netsh advfirewall firewall delete rule name=all program=\"{exe}\" " +
            $"& netsh advfirewall firewall add rule name=\"LolRemote\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            process?.WaitForExit(30_000);
        }
        catch (Win32Exception)
        {
            return "A Windows engedélykérést elutasítottad, ezért a tűzfal nem lett beállítva.";
        }
        catch (Exception ex)
        {
            return $"Nem sikerült: {ex.Message}";
        }
        return Status(fresh: true) == "ok" ? null : "A szabály elkészült, de a Windows még nem mutatja. Ha vírusirtód van, annak a tűzfalában is engedélyezd a LolRemote-ot.";
    }
}
