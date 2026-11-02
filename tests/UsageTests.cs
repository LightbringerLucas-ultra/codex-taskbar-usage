using System;
using System.Collections.Generic;
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
            Console.WriteLine("PASS: " + checks + " checks"); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
