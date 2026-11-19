using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class ApiCreditBalance
{
    const string Session = "codex-taskbar-openai-credit";
    const string Platform = "https://platform.openai.com/home";
    const string AppDataScript = @"npm\node_modules\@jackwener\opencli\dist\src\";
    static readonly string Node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\node.exe");
    static readonly string ScriptDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataScript);
    const string Common = " --session " + Session + " --window background";

    // Read the visible Credit remaining tile, without using browser storage or network internals.
    const string ExtractBalance = @"(() => {
      if (location.origin !== 'https://platform.openai.com' || location.pathname !== '/home') return null;
      const label = Array.from(document.querySelectorAll('body *')).find(e => e.children.length === 0 && e.textContent.trim() === 'Credit remaining' && e.getClientRects().length);
      for (let row = label && label.parentElement, i = 0; row && i < 4; row = row.parentElement, i++) {
        const amounts = Array.from(row.querySelectorAll('*')).filter(e => e.children.length === 0 && e.getClientRects().length)
          .map(e => e.textContent.trim()).filter(t => /^(?:US)?\$\s*[−-]?[0-9][0-9,]*\.[0-9]{2}$/.test(t));
        if (amounts.length === 1) return amounts[0];
      }
      return null;
    })()";

    decimal? amount;
    internal decimal? Amount { get { return amount; } }
    internal DateTime? LastRead { get; private set; }
    internal bool Stale { get; private set; }
    internal string Note { get; private set; }
    internal string Display {
        get {
            if (!amount.HasValue) return "--";
            return (amount.Value < 0 ? "-$" : "$") + Math.Abs(amount.Value).ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    internal void Load(string store)
    {
        MarkStale("等待读取 OpenAI Platform 的余额。");
        try {
            string[] record = File.ReadAllLines(Path.Combine(store, "api-credit.txt"));
            decimal value;
            DateTime read;
            if (record.Length == 2 && Decimal.TryParse(record[0], NumberStyles.Number, CultureInfo.InvariantCulture, out value)
                && DateTime.TryParseExact(record[1], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out read)) {
                amount = value;
                LastRead = read;
                MarkStale("显示上一次读取的余额，正在等待更新。");
            }
        } catch (IOException) { }
          catch (UnauthorizedAccessException) { }
    }

    internal void Update(decimal value, string store)
    {
        amount = value;
        LastRead = DateTime.UtcNow;
        Stale = false;
        Note = null;
        try {
            File.WriteAllLines(Path.Combine(store, "api-credit.txt"), new[] {
                value.ToString(CultureInfo.InvariantCulture), LastRead.Value.ToString("o", CultureInfo.InvariantCulture)
            });
        } catch (IOException) { }
          catch (UnauthorizedAccessException) { }
    }

    internal void MarkStale(string note)
    {
        Stale = true;
        Note = note;
    }

    internal static decimal Read()
    {
        CheckEdge();
        string profileArgument = ReadProfileArgument();
        EnsureBridge();
        bool opened = false;
        try {
            Run("open " + Platform, profileArgument, 16000);
            opened = true;
            Run("wait text \"Credit remaining\" --timeout 12000", profileArgument, 16000);
            // The title can render before the monetary value. Recheck the same page without reloading it.
            string readAmount = "eval \"" + ExtractBalance.Replace("\r", "").Replace("\n", " ") + "\"";
            for (int attempt = 0; attempt < 4; attempt++) {
                if (attempt > 0) Thread.Sleep(650);
                string result = Run(readAmount, profileArgument, 10000) as string;
                decimal balance;
                if (result != null && Regex.IsMatch(result, @"^(?:US)?\$\s*[−-]?[0-9][0-9,]*\.[0-9]{2}$")
                    && Decimal.TryParse(result.Replace("US", "").Replace("$", "").Replace("−", "-").Trim(),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out balance))
                    return balance;
            }
            throw new InvalidOperationException("OpenAI Platform 未显示可读取的美元余额。");
        } finally {
            if (opened) try { Run("close", profileArgument, 5000); } catch { }
        }
    }

    static string ReadProfileArgument()
    {
        const string name = "CODEX_TASKBAR_OPENCLI_PROFILE";
        string profile = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(name);
        if (String.IsNullOrWhiteSpace(profile)) return "";
        profile = profile.Trim();
        if (!Regex.IsMatch(profile, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z"))
            throw new InvalidOperationException("OpenCLI 配置名仅支持字母、数字、下划线、点和连字符。");
        return " --profile " + profile;
    }

    static void CheckEdge()
    {
        Process[] processes = Process.GetProcessesByName("msedge");
        try {
            if (processes.Length == 0) throw new InvalidOperationException("等待 Edge 打开并登录 OpenAI Platform。");
        } finally { foreach (var process in processes) process.Dispose(); }
    }

    static void EnsureBridge()
    {
        if (!File.Exists(Node) || !File.Exists(Path.Combine(ScriptDirectory, "main.js")))
            throw new InvalidOperationException("未找到本机的 OpenCLI。");
        if (BridgeListening()) return;
        using (var daemon = Process.Start(new ProcessStartInfo(Node, "\"" + Path.Combine(ScriptDirectory, "daemon.js") + "\"") {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        })) {
            for (int attempt = 0; attempt < 20; attempt++) {
                if (BridgeListening()) return;
                if (daemon.HasExited) break;
                Thread.Sleep(200);
            }
        }
        throw new InvalidOperationException("OpenCLI 后台连接未就绪。");
    }

    static bool BridgeListening()
    {
        foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (listener.Port == 19825) return true;
        return false;
    }

    static object Run(string command, string profileArgument, int timeout)
    {
        var args = "\"" + Path.Combine(ScriptDirectory, "main.js") + "\" browser " + command + Common + profileArgument;
        var start = new ProcessStartInfo(Node, args) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var process = Process.Start(start)) {
            process.BeginErrorReadLine();
            Task<string> pending = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeout) || !pending.Wait(1000)) {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                throw new InvalidOperationException("OpenAI Platform 余额查询超时，请稍后重试。");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException("请确认 Edge 已登录 OpenAI Platform，且 OpenCLI 浏览器连接可用。");
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(pending.Result);
            object ok, data;
            if (!result.TryGetValue("ok", out ok) || !Equals(ok, true) || !result.TryGetValue("data", out data))
                throw new InvalidOperationException("OpenAI Platform 余额暂时不可用。");
            return data;
        }
    }
}
