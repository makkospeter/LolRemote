using System.Text;
using LolRemote.MockLcu;
using Microsoft.AspNetCore.Mvc;

Console.OutputEncoding = Encoding.UTF8;

const int port = 54321;
var password = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
var lockDir = Path.Combine(Path.GetTempPath(), "LolRemoteMock");
var lockPath = Path.Combine(lockDir, "lockfile");

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();
builder.Services.AddSingleton<WebSocketHub>();
builder.Services.AddSingleton<MockEngine>();
builder.Services.AddSingleton<MockRunes>();
var app = builder.Build();

var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{password}"));
app.Use(async (context, next) =>
{
    if (context.Request.Headers.Authorization.ToString() != expectedAuth)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next(context);
});

app.UseWebSockets();

app.Map("/", async (HttpContext context, WebSocketHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    var protocol = context.WebSockets.WebSocketRequestedProtocols.Contains("wamp") ? "wamp" : null;
    using var socket = await context.WebSockets.AcceptWebSocketAsync(protocol);
    await hub.HandleAsync(socket, context.RequestAborted);
});

app.MapGet("/lol-gameflow/v1/gameflow-phase", (MockEngine engine) => Results.Json(engine.Phase));

app.MapGet("/lol-gameflow/v1/session", (MockEngine engine) => Results.Json(engine.GameflowSession(), MockEngine.JsonOptions));

app.MapGet("/lol-matchmaking/v1/ready-check", (MockEngine engine) =>
    engine.GetReadyCheckJson() is { } json ? Results.Content(json, "application/json") : Results.NotFound());

app.MapPost("/lol-matchmaking/v1/ready-check/accept", (MockEngine engine) =>
    engine.AcceptReadyCheck() ? Results.NoContent() : Results.BadRequest(new { message = "Nincs elfogadható meccs." }));

app.MapPost("/lol-matchmaking/v1/ready-check/decline", (MockEngine engine) =>
    engine.DeclineReadyCheck() ? Results.NoContent() : Results.BadRequest(new { message = "Nincs elutasítható meccs." }));

app.MapGet("/lol-champ-select/v1/session", (MockEngine engine) =>
    engine.GetSessionJson() is { } json ? Results.Content(json, "application/json") : Results.NotFound());

app.MapPatch("/lol-champ-select/v1/session/actions/{id:int}", (int id, ActionPatch body, MockEngine engine) =>
    ToResult(engine.PatchAction(id, body.ChampionId, body.Completed)));

app.MapPost("/lol-champ-select/v1/session/actions/{id:int}/complete", (int id, MockEngine engine) =>
    engine.StrictLock
        ? ToResult(engine.PatchAction(id, null, true, fromComplete: true))
        : Results.BadRequest(new { message = "Az igazi kliens ezzel a végponttal nem lockol (mock)." }));

app.MapGet("/lol-perks/v1/recommended-pages/champion/{championId:int}/position/{position}/map/{mapId:int}",
    (int championId, string position, int mapId, MockRunes runes) => Results.Json(runes.Recommended(championId, position), MockEngine.JsonOptions));

app.MapPatch("/lol-champ-select/v1/session/my-selection", (MySelectionPatch body, MockEngine engine) =>
    ToResult(engine.SetSpells(body.Spell1Id, body.Spell2Id)));

app.MapGet("/lol-champ-select/v1/pickable-champion-ids", (MockEngine engine) =>
    Results.Json(engine.GetAvailableIds("pick")));

app.MapGet("/lol-champ-select/v1/bannable-champion-ids", (MockEngine engine) =>
    Results.Json(engine.GetAvailableIds("ban")));

foreach (var verb in new[] { "request", "accept", "decline", "cancel" })
{
    app.MapPost($"/lol-champ-select/v1/session/trades/{{id:int}}/{verb}", (int id, MockEngine engine) =>
        ToResult(engine.SwapCommand(true, id, verb)));
    app.MapPost($"/lol-champ-select/v1/session/pick-order-swaps/{{id:int}}/{verb}", (int id, MockEngine engine) =>
        ToResult(engine.SwapCommand(false, id, verb)));
}

app.MapPost("/lolremote-mock/queue", (MockEngine engine) =>
{
    engine.StartQueue();
    return Results.NoContent();
});

app.MapPost("/lolremote-mock/exit", (IHostApplicationLifetime appLifetime) =>
{
    appLifetime.StopApplication();
    return Results.NoContent();
});

app.MapGet("/lol-game-data/assets/v1/champion-summary.json", () =>
    Results.Json(MockEngine.Champions.Select(c => new
    {
        id = c.Key,
        name = c.Value,
        alias = c.Value == "Nunu & Willump" ? "Nunu" : new string(c.Value.Where(char.IsLetter).ToArray())
    })));

app.MapGet("/lol-game-data/assets/v1/summoner-spells.json", () =>
    Results.Json(MockEngine.Spells.Select(s => new { id = s.Key, name = s.Value })));

app.MapGet("/lol-perks/v1/pages", (MockRunes runes) =>
    Results.Content(runes.PagesJson(), "application/json"));

app.MapGet("/lol-perks/v1/currentpage", (MockRunes runes) =>
    runes.CurrentJson() is { } json ? Results.Content(json, "application/json") : Results.NotFound());

app.MapPut("/lol-perks/v1/currentpage", ([FromBody] long pageId, MockRunes runes) =>
    ToResult(runes.SetCurrent(pageId)));

app.MapPost("/lol-perks/v1/pages", (RunePage body, MockRunes runes) =>
{
    var (error, json) = runes.Create(body);
    return error is null ? Results.Content(json!, "application/json") : Results.BadRequest(new { message = error });
});

app.MapPut("/lol-perks/v1/pages/{id:long}", (long id, RunePage body, MockRunes runes) =>
    ToResult(runes.Update(id, body)));

app.MapGet("/lol-perks/v1/perks", () =>
    Results.Json(MockRunes.Perks.Select(p => new { id = p.Key, name = p.Value })));

app.MapGet("/lol-perks/v1/styles", () =>
    Results.Json(MockRunes.Styles.Select(s => new { id = s.Key, name = s.Value })));

var engine = app.Services.GetRequiredService<MockEngine>();
engine.Practice = args.Contains("--practice");
engine.StrictLock = args.Contains("--strict-lock");
var lifetime = app.Lifetime;

lifetime.ApplicationStarted.Register(() =>
{
    Directory.CreateDirectory(lockDir);
    File.WriteAllText(lockPath, $"LeagueClient:{Environment.ProcessId}:{port}:{password}:http");
    PrintHelp();
    Out.Log($"Fut: http://127.0.0.1:{port}   lockfile: {lockPath}", ConsoleColor.DarkGray);
    if (engine.Practice) Out.Log("PRACTICE TOOL mód: nincs ready check, ban és ellenfél.", ConsoleColor.Yellow);

    _ = Task.Run(() => engine.RunAsync(lifetime.ApplicationStopping));
    _ = Task.Run(() =>
    {
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.M: engine.StartQueue(); break;
                case ConsoleKey.R: engine.Reset(); break;
                case ConsoleKey.I: engine.PrintStatus(); break;
                case ConsoleKey.H: PrintHelp(); break;
                case ConsoleKey.X: lifetime.StopApplication(); break;
            }
        }
    });
});

lifetime.ApplicationStopping.Register(() =>
{
    try { File.Delete(lockPath); } catch (Exception) { }
});

app.Run();

static IResult ToResult(string? error) =>
    error is null ? Results.NoContent() : Results.BadRequest(new { message = error });

static void PrintHelp()
{
    Out.Line("══════════════════════════════════════════════════════════════", ConsoleColor.DarkCyan);
    Out.Line(" ÁL-LEAGUE KLIENS (MOCK)", ConsoleColor.Cyan);
    Out.Line(" Ez az ablak helyettesíti a League klienst tesztelés közben.", ConsoleColor.Gray);
    Out.Line(" Itt csak a meccskeresést indítod. Minden döntést (elfogadás,", ConsoleColor.Gray);
    Out.Line(" ban, pick, lock, spell, rúna, csere) a TÁRSPROGRAMBAN vagy", ConsoleColor.Gray);
    Out.Line(" a TELEFONON hozol meg.", ConsoleColor.Gray);
    Out.Line("──────────────────────────────────────────────────────────────", ConsoleColor.DarkCyan);
    Out.Line(" [M] Meccskeresés indítása     [R] Alaphelyzet", ConsoleColor.White);
    Out.Line(" [I] Állapot kiírása           [H] Súgó        [X] Kilépés", ConsoleColor.White);
    Out.Line("══════════════════════════════════════════════════════════════", ConsoleColor.DarkCyan);
}
