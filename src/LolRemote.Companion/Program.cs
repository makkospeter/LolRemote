using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LolRemote.Companion;

var consoleMode = args.Contains("--console");
var headless = args.Contains("--selftest") || args.Contains("--probe") || args.Contains("--probe-calls");
var guiMode = !consoleMode && !headless;

if (consoleMode) NativeConsole.Attach();
try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }
try { if (consoleMode) Console.Title = "LolRemote"; } catch (Exception) { }

var probeArg = Array.IndexOf(args, "--probe");
if (probeArg >= 0)
{
    var probeFolder = probeArg + 1 < args.Length ? args[probeArg + 1] : Path.Combine(Environment.CurrentDirectory, "probe");
    Environment.Exit(await CounterProbe.RunAsync(probeFolder, CancellationToken.None));
}

var callsArg = Array.IndexOf(args, "--probe-calls");
if (callsArg >= 0 && callsArg + 2 < args.Length)
{
    Environment.Exit(await CounterProbe.RunCallsAsync(args[callsArg + 1], args[callsArg + 2], CancellationToken.None));
}

var isMock = args.Contains("--mock");
var lockfileArg = Array.IndexOf(args, "--lockfile");
string? fixedLockfile = lockfileArg >= 0 && lockfileArg + 1 < args.Length
    ? args[lockfileArg + 1]
    : isMock
        ? Path.Combine(Path.GetTempPath(), "LolRemoteMock", "lockfile")
        : null;

var debugArg = Array.IndexOf(args, "--debug-dir");
var explicitDebug = debugArg >= 0 && debugArg + 1 < args.Length;
var debugDump = explicitDebug ? new DebugDump(args[debugArg + 1]) : null;
if (debugDump is null && guiMode)
{
    try
    {
        var debugRoot = Path.Combine(AppPaths.Folder, "debug");
        Directory.CreateDirectory(debugRoot);
        foreach (var old in new DirectoryInfo(debugRoot).GetDirectories().OrderByDescending(d => d.Name).Skip(9))
        {
            try { old.Delete(true); } catch (Exception) { }
        }
        debugDump = new DebugDump(debugRoot);
    }
    catch (Exception)
    {
    }
}

if (args.Contains("--selftest"))
{
    AppPaths.Folder = Path.Combine(Path.GetTempPath(), "LolRemoteSelfTest");
    try { Directory.Delete(AppPaths.Folder, true); } catch (Exception) { }
}

Mutex? instanceMutex = null;
if (guiMode)
{
    instanceMutex = new Mutex(true, "LolRemote.SingleInstance", out var firstInstance);
    if (!firstInstance)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            await http.GetAsync($"http://127.0.0.1:{PhoneServer.Port}/__show");
        }
        catch (Exception)
        {
        }
        return;
    }
    EventLog.FilePath = AppPaths.PathFor("lolremote.log");
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        EventLog.WriteFile($"VÁRATLAN HIBA: {e.ExceptionObject}");
}

var settings = Settings.Load();
var templates = new TemplateStore();
var log = new EventLog();
var counters = new OpggCounters();
var ui = new ConsoleUi(log, isMock ? "mock kliens" : "League kliens", isMock);
var phone = new PhoneServer(log, () => ui.Controller);
ui.PhoneInfo = phone.Describe;
ui.Phone = phone;
phone.OpenBrowserOnFirstRun = consoleMode && !isMock && !args.Contains("--no-browser");

using var cts = new CancellationTokenSource();
if (consoleMode)
{
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
}

try
{
    await phone.StartAsync(cts.Token);
}
catch (Exception ex)
{
    log.Add($"A telefonos szerver nem indult el (port {PhoneServer.Port}): {ex.Message}", ConsoleColor.Red);
    log.Add("Lehet, hogy a LolRemote már fut egy másik ablakban.", ConsoleColor.Yellow);
}

var connectionTask = Task.Run(() => ConnectionLoopAsync(cts.Token));

var selfTestArg = Array.IndexOf(args, "--selftest");
if (selfTestArg >= 0)
{
    var logPath = selfTestArg + 1 < args.Length && !args[selfTestArg + 1].StartsWith("--")
        ? args[selfTestArg + 1]
        : Path.Combine(Environment.CurrentDirectory, "selftest-result.txt");
    var exitCode = await new SelfTest(phone, () => ui.Controller).RunAsync(logPath, cts.Token);
    Environment.Exit(exitCode);
}

if (guiMode)
{
    log.Add($"LolRemote {typeof(PhoneServer).Assembly.GetName().Version?.ToString(3)} elindult.", ConsoleColor.DarkGray);
    Exception? uiError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var window = new MainWindow($"http://127.0.0.1:{PhoneServer.Port}/desktop.html");
            phone.ShowRequested += window.BringToFrontSafe;
            Application.Run(window);
        }
        catch (Exception ex)
        {
            uiError = ex;
        }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (uiError is not null)
    {
        EventLog.WriteFile($"Ablak hiba: {uiError}");
        MessageBox.Show($"A LolRemote ablaka nem indult el:\n{uiError.Message}", "LolRemote", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    cts.Cancel();
    GC.KeepAlive(instanceMutex);
    Environment.Exit(0);
}

await ui.RunAsync(cts);
cts.Cancel();
try { await connectionTask; } catch (Exception) { }

Console.Clear();
Console.WriteLine("LolRemote leállt.");
Environment.Exit(0);

async Task ConnectionLoopAsync(CancellationToken ct)
{
    var waitingLogged = false;
    while (!ct.IsCancellationRequested)
    {
        var lockfilePath = fixedLockfile ?? LockfileInfo.DetectPath();
        var info = lockfilePath is null ? null : LockfileInfo.TryRead(lockfilePath);
        if (info is null)
        {
            if (!waitingLogged)
            {
                log.Add(fixedLockfile is null
                    ? "Várakozás a League kliensre (indítsd el a játékot)..."
                    : $"Várakozás a lockfile-ra: {fixedLockfile}", ConsoleColor.DarkGray);
                waitingLogged = true;
            }
            await WaitAsync(TimeSpan.FromSeconds(2), ct);
            continue;
        }
        waitingLogged = false;

        if (fixedLockfile is null) log.Add($"League kliens megtalálva: {lockfilePath}", ConsoleColor.DarkGray);
        await using var lcu = new LcuConnection(info);
        if (debugDump is not null)
        {
            lcu.EventReceived += debugDump.Write;
            if (explicitDebug) log.Add($"Diagnosztikai mentés: {debugDump.Folder}", ConsoleColor.DarkGray);
        }
        try
        {
            await lcu.ConnectAsync(ct);
            var controller = new ChampSelectController(lcu, log, settings, templates);
            if (debugDump is not null) controller.DebugWriter = debugDump.WriteRaw;
            if (debugDump is not null) counters.DebugWriter = debugDump.WriteRaw;
            controller.Counters = counters;
            controller.Changed += ui.MarkDirty;
            controller.Changed += phone.MarkDirty;
            await controller.LoadAsync(ct);
            ui.Controller = controller;
            phone.MarkDirty();
            await lcu.ListenAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            log.Add($"Kapcsolati hiba: {ex.Message}", ConsoleColor.Red);
        }

        ui.Controller = null;
        phone.MarkDirty();
        if (!ct.IsCancellationRequested) log.Add("Megszakadt a kapcsolat, újrapróbálom...", ConsoleColor.DarkGray);
        await WaitAsync(TimeSpan.FromSeconds(2), ct);
    }
}

static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
{
    try { await Task.Delay(delay, ct); }
    catch (OperationCanceledException) { }
}

static class NativeConsole
{
    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    public static void Attach()
    {
        if (!AttachConsole(-1)) AllocConsole();
    }
}
