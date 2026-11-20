using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;

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
    static double? Remaining(Dictionary<string, object> window) {
        var method = typeof(UsageWidget).GetMethod("Remaining", BindingFlags.Static | BindingFlags.NonPublic);
        return (double?)method.Invoke(null, new object[] { window });
    }
    static int Main() {
        try {
            var fiveInput = Window(300, 18); var weekInput = Window(10080, 44);
            Dictionary<string, object> five, week;
            Select(fiveInput, weekInput, out five, out week);
            Check(five == fiveInput && week == weekInput, "Plus: identify both windows");
            Check(Remaining(five) == 82 && Remaining(week) == 56, "remaining values");
            Select(weekInput, null, out five, out week);
            Check(five == null && week == weekInput, "Pro: weekly primary must not be called five-hour");
            Select(weekInput, fiveInput, out five, out week);
            Check(five == fiveInput && week == weekInput, "reversed order");
            Select(Window(10080, null), null, out five, out week);
            Check(five == null && week != null && Remaining(week) == null, "missing percentage stays unknown");
            Check(Remaining(Window(300, -5)) == 100 && Remaining(Window(300, 110)) == 0, "clamp percentages");
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
            Check(UsageWidget.IsClaudeCodeExecutable(@"C:\Users\Demo\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"), "npm Claude Code native launcher");
            Check(UsageWidget.IsClaudeCodeExecutable(@"C:\Users\Demo\.local\bin\claude.exe"), "standalone Claude Code installer");
            Check(!UsageWidget.IsClaudeCodeExecutable(@"C:\Program Files\WindowsApps\Claude_1.0.0_x64\app\claude.exe"), "Claude Desktop does not start API polling");
            Console.WriteLine("PASS: " + checks + " checks"); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
