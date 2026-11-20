using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class ApiCreditBalance
{
    static readonly string Session = "codex-taskbar-openai-credit-" + Guid.NewGuid().ToString("N").Substring(0, 10);
    const string Platform = "https://platform.openai.com/home";
    const string AppDataScript = @"npm\node_modules\@jackwener\opencli\dist\src\";
    static readonly string Node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\node.exe");
    static readonly string ScriptDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataScript);
    static readonly string Common = " --session " + Session + " --window background";

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

    internal static decimal Read(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        CheckEdge();
        string profileArgument = ReadProfileArgument();
        Process ownedBridge = null;
        bool sessionAttempted = false;
        try {
            ownedBridge = StartBridgeIfNeeded();
            if (ownedBridge != null) WaitForBridge(ownedBridge, cancellation);
            cancellation.ThrowIfCancellationRequested();
            sessionAttempted = true;
            Run("open " + Platform, profileArgument, 40000, cancellation);
            Run("wait text \"Credit remaining\" --timeout 15000", profileArgument, 24000, cancellation);
            // The title can render before the monetary value. Recheck the same page without reloading it.
            string readAmount = "eval \"" + ExtractBalance.Replace("\r", "").Replace("\n", " ") + "\"";
            for (int attempt = 0; attempt < 4; attempt++) {
                if (attempt > 0 && cancellation.WaitHandle.WaitOne(650))
                    cancellation.ThrowIfCancellationRequested();
                string result = Run(readAmount, profileArgument, 15000, cancellation) as string;
                decimal balance;
                if (result != null && Regex.IsMatch(result, @"^(?:US)?\$\s*[−-]?[0-9][0-9,]*\.[0-9]{2}$")
                    && Decimal.TryParse(result.Replace("US", "").Replace("$", "").Replace("−", "-").Trim(),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out balance))
                    return balance;
            }
            throw new InvalidOperationException("OpenAI Platform 未显示可读取的美元余额。");
        } finally {
            if (sessionAttempted) {
                try {
                    if (BridgeListening()) Run("close", profileArgument, 5000, CancellationToken.None);
                } catch { }
            }
            if (ownedBridge != null) {
                using (ownedBridge) ReleaseBridgeIfIdle(ownedBridge);
            }
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

    static Process StartBridgeIfNeeded()
    {
        if (!File.Exists(Node) || !File.Exists(Path.Combine(ScriptDirectory, "main.js")))
            throw new InvalidOperationException("未找到本机的 OpenCLI。");
        if (BridgeListening()) return null;
        return Process.Start(new ProcessStartInfo(Node, "\"" + Path.Combine(ScriptDirectory, "daemon.js") + "\"") {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    static void WaitForBridge(Process daemon, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 32; attempt++) {
            cancellation.ThrowIfCancellationRequested();
            Dictionary<string, object> status;
            if (TryReadBridgeStatus(out status)) {
                object profiles;
                if (status.TryGetValue("profiles", out profiles) && profiles is System.Collections.ICollection
                    && ((System.Collections.ICollection)profiles).Count > 0) return;
            } else if (daemon.HasExited) break;
            if (cancellation.WaitHandle.WaitOne(250)) cancellation.ThrowIfCancellationRequested();
        }
        throw new InvalidOperationException("OpenCLI 后台连接未就绪。");
    }

    static bool TryReadBridgeStatus(out Dictionary<string, object> status)
    {
        status = null;
        try {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:19825/status");
            request.Proxy = null;
            request.Timeout = 750;
            request.ReadWriteTimeout = 750;
            request.Headers.Add("X-OpenCLI", "1");
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream())) {
                status = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                return status != null;
            }
        } catch (WebException) { }
          catch (IOException) { }
          catch (ArgumentException) { }
        return false;
    }

    static bool IsEmptyList(Dictionary<string, object> status, string name)
    {
        object value;
        if (!status.TryGetValue(name, out value)) return false;
        var items = value as System.Collections.ICollection;
        return items != null && items.Count == 0;
    }

    static void ReleaseBridgeIfIdle(Process daemon)
    {
        // A pre-existing or currently shared bridge belongs to the user's other OpenCLI work.
        try {
            Dictionary<string, object> status;
            object pid, pending;
            if (daemon.HasExited) return;
            if (!TryReadBridgeStatus(out status)) {
                if (!BridgeListening()) KillProcess(daemon); // Cancelled before startup completed.
                return;
            }
            if (status.TryGetValue("pid", out pid) && Equals(pid, daemon.Id)
                && status.TryGetValue("pending", out pending) && Equals(pending, 0)
                && IsEmptyList(status, "sessionLeases") && IsEmptyList(status, "siteSlots")) {
                KillProcess(daemon);
                daemon.WaitForExit(1000);
            }
        } catch (InvalidOperationException) { }
          catch (System.ComponentModel.Win32Exception) { }
    }

    static bool BridgeListening()
    {
        foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (listener.Port == 19825) return true;
        return false;
    }

    static object Run(string command, string profileArgument, int timeout, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var args = "\"" + Path.Combine(ScriptDirectory, "main.js") + "\" browser " + command + Common + profileArgument;
        var start = new ProcessStartInfo(Node, args) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var process = Process.Start(start)) {
            try {
                process.BeginErrorReadLine();
                Task<string> pending = process.StandardOutput.ReadToEndAsync();
                var elapsed = Stopwatch.StartNew();
                while (!process.WaitForExit(100)) {
                    cancellation.ThrowIfCancellationRequested();
                    if (elapsed.ElapsedMilliseconds >= timeout)
                        throw new InvalidOperationException("OpenCLI 查询步骤超时：" + command.Split(' ')[0] + "。");
                }
                cancellation.ThrowIfCancellationRequested();
                if (!pending.Wait(1000))
                    throw new InvalidOperationException("OpenCLI 未能完整返回余额查询结果。");
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("请确认 Edge 已登录 OpenAI Platform，且 OpenCLI 浏览器连接可用。");
                var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(pending.Result);
                object ok, data;
                if (!result.TryGetValue("ok", out ok) || !Equals(ok, true) || !result.TryGetValue("data", out data))
                    throw new InvalidOperationException("OpenAI Platform 余额暂时不可用。");
                return data;
            } finally { KillProcess(process); }
        }
    }

    static void KillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
