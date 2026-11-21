using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

internal static class UsageTests
{
    static int checks;
    static void Check(bool condition, string description) {
        checks++;
        if (!condition) throw new Exception("FAIL: " + description);
    }
    static Dictionary<string, object> Window(int? mins, object used) {
        return new Dictionary<string, object> { { "windowDurationMins", mins }, { "usedPercent", used } };
    }
    static void Select(Dictionary<string, object> a, Dictionary<string, object> b, out Dictionary<string, object> five, out Dictionary<string, object> week) {
        UsageWidget.SelectWindows(new Dictionary<string, object> { { "primary", a }, { "secondary", b } }, out five, out week);
    }
    static void CheckThrows<T>(Action action, string description) where T : Exception {
        try { action(); }
        catch (T) { Check(true, description); return; }
        Check(false, description);
    }

    static void CheckNumbers() {
        var oldCulture = Thread.CurrentThread.CurrentCulture;
        try {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Check(UsageWidget.Remaining(Window(300, "18.5")) == 81.5, "protocol decimal strings use a dot in every locale");
            Check(UsageWidget.Remaining(Window(300, 18.5m)) == 81.5, "native JSON decimal values keep their precision");
            CheckThrows<FormatException>(() => UsageWidget.Remaining(Window(300, false)), "a boolean is not a usage percentage");
            CheckThrows<FormatException>(() => UsageWidget.Remaining(Window(300, "NaN")), "NaN cannot be displayed as a percentage");
            CheckThrows<FormatException>(() => UsageWidget.Remaining(Window(300, Double.PositiveInfinity)), "infinity is invalid");
            decimal value;
            Check(ApiCreditBalance.TryParseAmount("$1,234.56", out value) && value == 1234.56m, "grouped dollars");
            Check(ApiCreditBalance.TryParseAmount("-$1.25", out value) && value == -1.25m, "leading negative sign");
            Check(ApiCreditBalance.TryParseAmount("US$ −1,234.56", out value) && value == -1234.56m, "Unicode minus after currency");
            Check(ApiCreditBalance.TryParseAmount("−US$ 1.25", out value) && value == -1.25m, "Unicode minus before currency");
            Check(ApiCreditBalance.TryParseAmount("$0.00", out value) && value == 0, "zero is a valid balance");
            Check(!ApiCreditBalance.TryParseAmount(null, out value) && !ApiCreditBalance.TryParseAmount("$1,2.34", out value)
                && !ApiCreditBalance.TryParseAmount("-$-1.25", out value), "missing or malformed amounts are rejected");
        } finally { Thread.CurrentThread.CurrentCulture = oldCulture; }
    }

    static void CheckStateFiles(string directory) {
        string settings = Path.Combine(directory, "settings.json");
        Check(Program.TryWriteState(settings, "{\"offset\":152}"), "create settings");
        using (File.Open(settings, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(!Program.TryWriteState(settings, "{\"offset\":200}"), "a locked file does not interrupt the widget");
        Check(File.ReadAllText(settings) == "{\"offset\":152}", "failed save preserves the previous settings");
        Check(Program.TryWriteState(settings, "{\"offset\":200}") && File.ReadAllText(settings) == "{\"offset\":200}", "retry after unlock");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "temporary files are cleaned up");

        string cache = Path.Combine(directory, "api-credit.txt");
        DateTime previous = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.WriteAllLines(cache, new[] { "12.30", previous.ToString("o", CultureInfo.InvariantCulture) });
        var balance = new ApiCreditBalance();
        balance.Load(directory);
        Check(balance.Amount == 12.30m && balance.Stale && balance.LastRead == previous, "old cache format still loads");
        using (File.Open(cache, FileMode.Open, FileAccess.Read, FileShare.None)) {
            balance.Update(-1.25m, directory);
            Check(!balance.FlushCache(directory), "wait to release the worker while its last balance cannot be saved");
        }
        Check(balance.Display == "-$1.25" && !balance.Stale, "new reading stays available in memory if the cache is busy");
        var reloaded = new ApiCreditBalance();
        reloaded.Load(directory);
        Check(reloaded.Amount == 12.30m && reloaded.LastRead == previous, "locked cache retains its previous complete reading");
        DateTime? lastRead = balance.LastRead;
        Check(balance.FlushCache(directory) && balance.LastRead == lastRead, "retry a pending balance without changing its timestamp");
        reloaded.Load(directory);
        Check(reloaded.Display == "-$1.25" && reloaded.LastRead == balance.LastRead && reloaded.Stale, "new cache survives restart");
    }

    static void CheckIdleReadings(string directory) {
        string statusPath = Path.Combine(directory, "status.json");
        var json = new JavaScriptSerializer();
        DateTime updated = new DateTime(2026, 1, 2, 4, 5, 6, DateTimeKind.Local);
        string timestamp = updated.ToString("o", CultureInfo.InvariantCulture);
        string reset = updated.AddDays(1).ToString("o", CultureInfo.InvariantCulture);
        File.WriteAllText(statusPath, json.Serialize(new {
            hasFiveHour = false, hasWeekly = true, weeklyRemaining = 81.5m, weeklyReset = reset,
            updated = timestamp, apiCredit = "-$2.50", apiCreditUpdated = updated.ToUniversalTime(), offset = 233, hideCount = 4
        }));
        // The hidden form never enters a message loop or queries a real client.
        using (File.Open(Path.Combine(directory, "api-credit.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(Path.Combine(directory, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var widget = new UsageWidget(directory)) {
            using (File.Open(statusPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(!widget.TryRestorePausedUsage(), "retry a locked handoff");
            Check(widget.TryRestorePausedUsage() && widget.WriteStatus(true), "resume from the last hidden status");
            var status = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(statusPath));
            Check(Equals(status["stale"], true) && Equals(status["weeklyRemaining"], 81.5m)
                && Equals(status["hasWeekly"], true) && Equals(status["hasFiveHour"], false)
                && status["fiveHourRemaining"] == null, "keep a weekly-only reading and its stale marker");
            Check(Equals(status["updated"], timestamp) && Equals(status["weeklyReset"], reset), "preserve read and reset times");
            Check(Equals(status["apiCredit"], "-$2.50") && Equals(status["offset"], 233), "restore balance and position when their files are busy");
            Check(Equals(status["hideCount"], 4), "keep the hide counter across automatic worker restarts");
            File.WriteAllText(statusPath, json.Serialize(new { updated = timestamp, hasWeekly = true, weeklyRemaining = 1 }));
            Check(widget.TryRestorePausedUsage() && widget.WriteStatus(true)
                && Equals(json.Deserialize<Dictionary<string, object>>(File.ReadAllText(statusPath))["weeklyRemaining"], 81.5m),
                "a delayed restore cannot replace a reading already in memory");
            widget.Close();
        }
    }

    // The test executable also acts as a local app-server speaking the normal line protocol.
    static int RunFixtureServer() {
        string mode = File.ReadAllText("fixture-mode");
        string init = Console.ReadLine();
        if (init == null || !init.Contains("\"initialize\"")) return 2;
        Console.WriteLine("{\"method\":\"notification\"}");
        Console.WriteLine("{\"id\":1,\"result\":{}}");
        string ready = Console.ReadLine(), request = Console.ReadLine();
        if (ready == null || !ready.Contains("\"initialized\"") || request == null || !request.Contains("account/rateLimits/read")) return 3;
        using (var process = Process.GetCurrentProcess())
            if (!Program.TryWriteState("fixture-pid", process.Id.ToString(CultureInfo.InvariantCulture))) return 4;
        if (mode == "wait") { Thread.Sleep(15000); return 5; }
        if (mode == "notify") {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < 15000) { Console.WriteLine("{\"method\":\"notification\"}"); Thread.Sleep(5); }
            return 5;
        }
        string limits = "{\"primary\":{\"windowDurationMins\":10080,\"usedPercent\":18.5}}";
        string legacy = "{\"primary\":{\"windowDurationMins\":300,\"usedPercent\":10}}";
        string result = mode == "legacy" ? "\"rateLimits\":" + legacy
            : "\"rateLimits\":" + legacy + ",\"rateLimitsByLimitId\":{\"codex\":" + limits + "}";
        Console.WriteLine("{\"id\":2,\"result\":{" + result + "}}");
        return 0;
    }

    static bool FixtureExited(string pidPath) {
        int pid = Int32.Parse(File.ReadAllText(pidPath), CultureInfo.InvariantCulture);
        try { using (var process = Process.GetProcessById(pid)) return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    static void CheckQueries(string directory) {
        string executable = Assembly.GetExecutingAssembly().Location;
        string mode = Path.Combine(directory, "fixture-mode"), pid = Path.Combine(directory, "fixture-pid");
        File.WriteAllText(mode, "normal");
        var limits = UsageWidget.FetchUsage(executable, directory, CancellationToken.None).GetAwaiter().GetResult();
        Dictionary<string, object> five, week;
        UsageWidget.SelectWindows(limits, out five, out week);
        Check(five == null && UsageWidget.Remaining(week) == 81.5, "ignore notifications and prefer the Codex bucket");

        File.Delete(pid);
        File.WriteAllText(mode, "wait");
        using (var cancel = new CancellationTokenSource()) {
            var query = UsageWidget.FetchUsage(executable, directory, cancel.Token);
            bool started = SpinWait.SpinUntil(() => File.Exists(pid), 5000);
            cancel.Cancel();
            CheckThrows<OperationCanceledException>(() => query.GetAwaiter().GetResult(), "cancel an in-flight query");
            Check(started, "query has reached the waiting server");
            Check(FixtureExited(pid), "cancelled query leaves no child process");
        }

        File.Delete(pid);
        File.WriteAllText(mode, "notify");
        CheckThrows<TimeoutException>(() => UsageWidget.FetchUsage(executable, directory, CancellationToken.None, 1500)
            .GetAwaiter().GetResult(), "notifications do not extend the shared deadline");
        Check(FixtureExited(pid), "timed-out query leaves no child process");

        File.WriteAllText(mode, "legacy");
        limits = UsageWidget.FetchUsage(executable, directory, CancellationToken.None).GetAwaiter().GetResult();
        UsageWidget.SelectWindows(limits, out five, out week);
        Check(UsageWidget.Remaining(five) == 90 && week == null, "retry succeeds with the legacy single-bucket response");
    }

    [STAThread]
    static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "app-server") return RunFixtureServer();
        try {
            var fiveInput = Window(300, 18); var weekInput = Window(10080, 44);
            Dictionary<string, object> five, week;
            Select(fiveInput, weekInput, out five, out week);
            Check(five == fiveInput && week == weekInput, "Plus: identify both windows");
            Check(UsageWidget.Remaining(five) == 82 && UsageWidget.Remaining(week) == 56, "remaining values");
            Select(weekInput, null, out five, out week);
            Check(five == null && week == weekInput, "Pro: weekly primary must not be called five-hour");
            Select(weekInput, fiveInput, out five, out week);
            Check(five == fiveInput && week == weekInput, "reversed order");
            Select(Window(10080, null), null, out five, out week);
            Check(five == null && week != null && UsageWidget.Remaining(week) == null, "missing percentage stays unknown");
            Check(UsageWidget.Remaining(Window(300, -5)) == 100 && UsageWidget.Remaining(Window(300, 110)) == 0, "clamp percentages");
            bool rejected = false;
            try { Select(Window(60, 2), Window(null, 1), out five, out week); }
            catch (Exception) { rejected = true; }
            Check(rejected, "unknown durations are not mislabeled");
            Check(UsageWidget.ApiAmountColor(null).ToArgb() == Color.FromArgb(153, 156, 164).ToArgb(), "missing balance is gray");
            Check(UsageWidget.ApiAmountColor(0m) == UsageWidget.ApiAmountColor(33.99m)
                && UsageWidget.ApiAmountColor(33.99m).ToArgb() == Color.FromArgb(255, 116, 119).ToArgb(), "under 34 USD is red");
            Check(UsageWidget.ApiAmountColor(34m) == UsageWidget.ApiAmountColor(66.99m)
                && UsageWidget.ApiAmountColor(66.99m).ToArgb() == Color.FromArgb(255, 209, 102).ToArgb(), "34 to 66.99 USD is yellow");
            Check(UsageWidget.ApiAmountColor(67m).ToArgb() == Color.FromArgb(125, 221, 183).ToArgb(), "67 USD and up is green");
            Check(ClientProcesses.IsClaudeCodeExecutable(@"C:\Users\Demo\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"), "npm Claude Code native launcher");
            Check(ClientProcesses.IsClaudeCodeExecutable(@"C:\Users\Demo\.local\bin\claude.exe"), "standalone Claude Code installer");
            Check(!ClientProcesses.IsClaudeCodeExecutable(@"C:\Program Files\WindowsApps\Claude_1.0.0_x64\app\claude.exe"), "Claude Desktop does not start API polling");
            Check(!ClientProcesses.IsClaudeCodeExecutable(null), "a process that disappeared during inspection is ignored");
            Check(UsageWidget.FitTaskbarLeft(8, 801, 228, 448, 581) == 220, "fit both readouts to the left of Start");
            Check(UsageWidget.FitTaskbarLeft(8, 650, 900, 448, 700) == 202, "clamp a saved position even when only the quota fits");
            Check(UsageWidget.FitTaskbarLeft(8, 600, -100, 448, 0) == 8, "clamp a position left of the taskbar");
            Check(UsageWidget.FitTaskbarLeft(8, 400, 228, 448, 581) == null, "hide when even the minimum width would overlap Start");
            CheckNumbers();
            string directory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { CheckStateFiles(directory); CheckIdleReadings(directory); CheckQueries(directory); }
            finally {
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
            Console.WriteLine("PASS: " + checks + " checks"); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
