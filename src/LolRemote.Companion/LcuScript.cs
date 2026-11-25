using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace LolRemote.Companion;

public static class LcuScript
{
    public static async Task<int> RunAsync(string scriptFile, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var summary = new StringBuilder();
        var path = LockfileInfo.DetectPath();
        var info = path is null ? null : LockfileInfo.TryRead(path);
        if (info is null)
        {
            await File.WriteAllTextAsync(Path.Combine(outDir, "summary.txt"), "NINCS LOCKFILE - fut a League kliens?");
            return 1;
        }
        await using var lcu = new LcuConnection(info);
        using var companion = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8765"), Timeout = TimeSpan.FromSeconds(60) };
        string? code = null;

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(scriptFile));
        var index = 0;
        foreach (var step in doc.RootElement.EnumerateArray())
        {
            var name = step.TryGetProperty("name", out var n) ? n.GetString() ?? "step" : "step";
            var repeat = step.TryGetProperty("repeat", out var r) ? r.GetInt32() : 1;
            var waitMs = step.TryGetProperty("waitMs", out var w) ? w.GetInt32() : 0;
            for (var i = 0; i < repeat; i++)
            {
                index++;
                if (waitMs > 0) await Task.Delay(waitMs);
                var label = repeat > 1 ? $"{name}-{i}" : name;
                if (step.TryGetProperty("skipIfLobbyCustom", out var sk) && sk.ValueKind == JsonValueKind.True)
                {
                    using var check = await lcu.GetRawAsync("/lol-lobby/v2/lobby");
                    if (Lookup(await check.Content.ReadAsStringAsync(), "gameConfig.isCustom") == "true")
                    {
                        summary.AppendLine($"{index:000} {label}: kihagyva (mar custom lobby)");
                        continue;
                    }
                }
                string status;
                string body;
                try
                {
                    if (step.TryGetProperty("hoverFirst", out _))
                    {
                        code ??= await PairCodeAsync(companion);
                        var list = await CompanionCmdAsync(companion, code, "{\"cmd\":\"champions\"}");
                        using var listDoc = JsonDocument.Parse(list);
                        var first = listDoc.RootElement.GetProperty("data").EnumerateArray().First();
                        var id = first.GetProperty("id").GetInt32();
                        body = $"elso valaszthato: {first.GetProperty("name").GetString()} ({id}) -> " + await CompanionCmdAsync(companion, code, $"{{\"cmd\":\"hover\",\"championId\":{id}}}");
                        status = "200";
                    }
                    else if (step.TryGetProperty("killOwnCustomGame", out _))
                    {
                        using var flow = await lcu.GetRawAsync("/lol-gameflow/v1/session");
                        var flowJson = await flow.Content.ReadAsStringAsync();
                        var isCustom = Lookup(flowJson, "gameData.isCustomGame") == "true";
                        var queueId = Lookup(flowJson, "gameData.queue.id");
                        var phase = Lookup(flowJson, "phase");
                        if (isCustom && queueId == "3270" && phase is "\"InProgress\"" or "\"GameStart\"")
                        {
                            var killed = 0;
                            foreach (var p in System.Diagnostics.Process.GetProcessesByName("League of Legends"))
                            {
                                try { p.Kill(); killed++; } catch (Exception) { }
                            }
                            body = $"custom Mayhem jatek leallitva ({killed} folyamat)";
                        }
                        else
                        {
                            body = $"nem a teszt custom jateka (custom={isCustom}, queue={queueId}, phase={phase}) - nem nyultam hozza";
                        }
                        status = "200";
                    }
                    else if (step.TryGetProperty("cancelVerify", out _))
                    {
                        var log = new StringBuilder();
                        var finalPhase = "";
                        for (var attempt = 0; attempt < 6; attempt++)
                        {
                            using (var cancel = await lcu.PostAsync("/lol-lobby/v1/lobby/custom/cancel-champ-select"))
                                log.Append($"cancel:{(int)cancel.StatusCode} ");
                            await Task.Delay(700);
                            using var ph = await lcu.GetRawAsync("/lol-gameflow/v1/gameflow-phase");
                            finalPhase = (await ph.Content.ReadAsStringAsync()).Trim('"');
                            log.Append($"phase:{finalPhase} ");
                            if (finalPhase is "Lobby" or "None") break;
                        }
                        using (var leave = await lcu.DeleteAsync("/lol-lobby/v2/lobby"))
                            log.Append($"leave:{(int)leave.StatusCode}");
                        body = log.ToString();
                        status = finalPhase is "Lobby" or "None" ? "200" : "FAIL";
                    }
                    else if (step.TryGetProperty("cmd", out var cmd))
                    {
                        code ??= await PairCodeAsync(companion);
                        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cmd")
                        {
                            Content = new StringContent(cmd.GetRawText(), Encoding.UTF8, "application/json")
                        };
                        request.Headers.Add("X-LolRemote-Code", code ?? "");
                        request.Headers.Add("X-LolRemote-Client", "script");
                        using var response = await companion.SendAsync(request);
                        status = ((int)response.StatusCode).ToString();
                        body = await response.Content.ReadAsStringAsync();
                    }
                    else if (step.TryGetProperty("state", out _))
                    {
                        code ??= await PairCodeAsync(companion);
                        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
                        request.Headers.Add("X-LolRemote-Code", code ?? "");
                        request.Headers.Add("X-LolRemote-Client", "script");
                        using var response = await companion.SendAsync(request);
                        status = ((int)response.StatusCode).ToString();
                        body = await response.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        var method = step.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
                        var url = step.GetProperty("path").GetString()!;
                        object? payload = step.TryGetProperty("body", out var b) ? b : null;
                        using var response = method switch
                        {
                            "POST" => await lcu.PostAsync(url, payload),
                            "PATCH" => await lcu.PatchAsync(url, payload!),
                            "PUT" => await lcu.PutAsync(url, payload!),
                            "DELETE" => await lcu.DeleteAsync(url),
                            _ => await lcu.GetRawAsync(url)
                        };
                        status = ((int)response.StatusCode).ToString();
                        body = await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception ex)
                {
                    status = "EXC";
                    body = ex.Message;
                }
                summary.AppendLine($"{index:000} {label}: {status} {(body.Length > 300 ? body[..300] + "..." : body)}");
                await File.WriteAllTextAsync(Path.Combine(outDir, $"{index:000}-{Safe(label)}.json"), body);
                await File.WriteAllTextAsync(Path.Combine(outDir, "summary.txt"), summary.ToString());
                var stop = false;
                if (step.TryGetProperty("requireOk", out var ro) && ro.ValueKind == JsonValueKind.True && !status.StartsWith('2'))
                    stop = true;
                if (step.TryGetProperty("require", out var req))
                {
                    var actual = Lookup(body, req.GetProperty("path").GetString()!);
                    var expected = req.GetProperty("equals").GetRawText();
                    if (actual != expected) stop = true;
                }
                if (stop)
                {
                    summary.AppendLine($"MEGALLITVA a(z) {label} lepesnel - a feltetel nem teljesult.");
                    var cleanup = step.TryGetProperty("onFail", out var of) ? of : default;
                    if (cleanup.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in cleanup.EnumerateArray())
                        {
                            try
                            {
                                using var resp = await lcu.PostAsync(c.GetString()!);
                                summary.AppendLine($"  takaritas {c.GetString()}: {(int)resp.StatusCode}");
                            }
                            catch (Exception ex)
                            {
                                summary.AppendLine($"  takaritas hiba: {ex.Message}");
                            }
                        }
                    }
                    await File.WriteAllTextAsync(Path.Combine(outDir, "summary.txt"), summary.ToString());
                    return 2;
                }
            }
        }
        summary.AppendLine("KESZ");
        await File.WriteAllTextAsync(Path.Combine(outDir, "summary.txt"), summary.ToString());
        return 0;
    }

    static async Task<string> CompanionCmdAsync(HttpClient companion, string? code, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cmd")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-LolRemote-Code", code ?? "");
        request.Headers.Add("X-LolRemote-Client", "script");
        using var response = await companion.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    static string Lookup(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            if (path.Length == 0) return el.GetRawText();
            foreach (var part in path.Split('.'))
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(part, out el)) return "<nincs>";
            }
            return el.GetRawText();
        }
        catch (Exception)
        {
            return "<hibas json>";
        }
    }

    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));

    static async Task<string?> PairCodeAsync(HttpClient companion)
    {
        try
        {
            var info = await companion.GetFromJsonAsync<JsonElement>("/setup-info");
            return info.TryGetProperty("code", out var c) ? c.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
