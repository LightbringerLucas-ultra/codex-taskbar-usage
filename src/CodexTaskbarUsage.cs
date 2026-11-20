using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job, int info, IntPtr buffer, uint size, out uint returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool MoveFileEx(string source, string destination, uint flags);
    const string TaskName = "外置大脑剩余";
    const string SupervisorMutex = "Local\\CodexTaskbarUsage_Supervisor_JYH";
    internal const int IdleExitCode = 74;
    static readonly string Executable = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).Location;
    internal static readonly string DataDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Executable), "..", "work", "runtime"));
    internal static readonly string JobState = ReadJobState();
    internal static readonly EventWaitHandle ExitSignal = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\CodexTaskbarUsage_Exit_JYH");
    static readonly string Store = DataDirectory;
    internal static void KillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static bool TryWriteState(string path, string contents)
    {
        // Publish a complete file in one rename, including the first write.
        const uint ReplaceExisting = 1;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, contents);
            if (MoveFileEx(temporary, path, ReplaceExisting)) return true;
        } catch (IOException) { }
          catch (UnauthorizedAccessException) { }
        try { File.Delete(temporary); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }
    internal static void Log(string message)
    {
        try {
            Directory.CreateDirectory(Store);
            string path = Path.Combine(Store, "lifecycle.log");
            string line = DateTime.Now.ToString("o") + " " + message + Environment.NewLine;
            if (File.Exists(path) && new FileInfo(path).Length > 65536) File.WriteAllText(path, line);
            else File.AppendAllText(path, line);
        } catch { }
    }
    [STAThread]
    static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--exit") >= 0) {
            ExitSignal.Set();
            Directory.CreateDirectory(Store);
            File.WriteAllText(Path.Combine(Store, "exit.request"), DateTime.Now.ToString("o"));
            return;
        }
        // The Windows scheduler launches outside the Codex/MSIX process jobs.
        bool directWorker = Array.IndexOf(args, "--worker") >= 0 || Array.IndexOf(args, "--verify") >= 0;
        if (!directWorker && Array.IndexOf(args, "--scheduled") < 0 && Array.IndexOf(args, "--detached") < 0) {
            try {
                ScheduledTask().Run(null);
                Log("launch delegated to Windows scheduler; sourceJob=" + JobState);
                return;
            } catch (Exception ex) { Log("scheduler launch unavailable: " + ex.Message); Environment.ExitCode = 76; return; }
        }
        if (!directWorker) { Supervise(); return; }
        RunWorker();
    }

    static void RunWorker()
    {
        bool created;
        using (var mutex = new Mutex(true, "Local\\CodexTaskbarUsage_JYH", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Log("worker running pid=" + Process.GetCurrentProcess().Id + " job=" + JobState);
            try {
                using (var widget = new UsageWidget())
                using (var context = new ApplicationContext(widget))
                    Application.Run(context);
            }
            catch (Exception ex) { Log("worker exception: " + ex); Environment.ExitCode = 75; }
        }
    }
    static dynamic ScheduledTask()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        service.Connect();
        return service.GetFolder("\\").GetTask(TaskName);
    }
    internal static bool StartupEnabled()
    {
        try {
            dynamic definition = ScheduledTask().Definition;
            for (int i=1; i<=definition.Triggers.Count; i++)
                if (definition.Triggers[i].Type == 9) return definition.Triggers[i].Enabled;
        } catch { }
        return false;
    }
    internal static void SetStartup(bool enabled)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        dynamic definition = folder.GetTask(TaskName).Definition;
        for (int i=1; i<=definition.Triggers.Count; i++)
            if (definition.Triggers[i].Type == 9) definition.Triggers[i].Enabled = enabled;
        folder.RegisterTaskDefinition(TaskName, definition, 6, definition.Principal.UserId, null, 3);
    }
    static void Supervise()
    {
        bool created;
        using (var mutex = new Mutex(true, SupervisorMutex, out created)) {
            if (!created) return;
            ExitSignal.Reset();
            Directory.CreateDirectory(Store);
            if (File.Exists(Path.Combine(Store, "exit.request"))) File.Delete(Path.Combine(Store, "exit.request"));
            int restartCount = 0, quickFailures = 0;
            bool resume = false;
            Log("supervisor started pid=" + Process.GetCurrentProcess().Id + " job=" + JobState);
            while (!ExitSignal.WaitOne(0)) {
                DateTime started = DateTime.UtcNow;
                try {
                    int code;
                    string arguments = resume ? "--worker --supervised --resume" : "--worker --supervised";
                    using (var child = ExistingWorker() ?? Process.Start(new ProcessStartInfo(Executable, arguments) {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(Executable)
                    })) {
                        WriteWatchdog(child.Id, restartCount);
                        Log("worker started pid=" + child.Id + " restartCount=" + restartCount);
                        child.WaitForExit();
                        code = child.ExitCode;
                    }
                    Log("worker exited code=" + code);
                    if (code == 0 || ExitSignal.WaitOne(0)) break;
                    if (code == IdleExitCode) {
                        WriteWatchdog(null, restartCount);
                        if (!WaitForClient()) break;
                        resume = true;
                        quickFailures = 0;
                        continue;
                    }
                } catch (Exception ex) { Log("supervisor exception: " + ex.Message); }
                restartCount++;
                quickFailures = (DateTime.UtcNow - started).TotalSeconds < 10 ? quickFailures + 1 : 0;
                if (ExitSignal.WaitOne(Math.Min(30000, 1000 * (1 << Math.Min(quickFailures, 5))))) break;
            }
            Log("supervisor stopped");
        }
    }
    internal static bool HasSupervisor()
    {
        Mutex supervisor;
        try {
            if (!Mutex.TryOpenExisting(SupervisorMutex, out supervisor)) return false;
            supervisor.Dispose();
            return true;
        } catch (UnauthorizedAccessException) { return false; }
    }
    static void WriteWatchdog(int? workerPid, int restartCount)
    {
        using (var supervisor = Process.GetCurrentProcess()) {
            if (!TryWriteState(Path.Combine(Store, "watchdog.json"), new JavaScriptSerializer().Serialize(new {
                supervisorPid = supervisor.Id, workerPid = workerPid, restartCount = restartCount,
                started = DateTime.Now.ToString("o"), job = JobState
            }))) Log("could not save watchdog.json");
        }
    }
    static bool WaitForClient()
    {
        string exitRequest = Path.Combine(Store, "exit.request");
        while (!ExitSignal.WaitOne(0) && !File.Exists(exitRequest)) {
            try { if (ClientProcesses.Read() != Clients.None) return true; }
            catch (System.ComponentModel.Win32Exception) { }
            if (ExitSignal.WaitOne(1000)) break;
        }
        return false;
    }
    static Process ExistingWorker()
    {
        Process candidate = null;
        try {
            var previous = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Store, "watchdog.json")));
            int pid = Convert.ToInt32(previous["workerPid"]);
            if (pid <= 0) return null;
            candidate = Process.GetProcessById(pid);
            if (candidate.Id != Process.GetCurrentProcess().Id && !candidate.HasExited &&
                String.Equals(candidate.MainModule.FileName, Executable, StringComparison.OrdinalIgnoreCase)) {
                Log("adopting existing worker pid=" + candidate.Id);
                return candidate;
            }
        } catch { }
        if (candidate != null) candidate.Dispose();
        return null;
    }
    static string ReadJobState()
    {
        IntPtr data = Marshal.AllocHGlobal(144);
        try {
            uint returned;
            if (QueryInformationJobObject(IntPtr.Zero, 9, data, 144, out returned))
                return "0x" + Marshal.ReadInt32(data,16).ToString("X");
            return "none (error=" + Marshal.GetLastWin32Error() + ")";
        } finally { Marshal.FreeHGlobal(data); }
    }
}

internal sealed class UsageWidget : Form
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW", SetLastError=true)] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x,int y) { X=x; Y=y; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int X, Y; public SIZE(int x,int y) { X=x; Y=y; } }
    [StructLayout(LayoutKind.Sequential, Pack=1)] struct BLEND { public byte Operation, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dc, ref POINT position, ref SIZE size, IntPtr source, ref POINT origin, uint key, ref BLEND blend, uint flags);

    readonly string store;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    const string WidgetTitle = "外置大脑剩余";
    readonly ToolTip tip = new ToolTip();
    readonly System.Windows.Forms.Timer locationTimer = new System.Windows.Forms.Timer();
    readonly ApiCreditBalance apiCredit = new ApiCreditBalance();
    Bitmap claudeIcon, codexIcon;
    bool stale = true, dragging;
    int offset = 152;
    int dragOrigin;
    Point dragMouse;
    double? fiveLeft, weekLeft;
    bool hasFive, hasWeek;
    bool WeeklyOnly { get { return hasWeek && !hasFive; } }
    DateTime? lastUpdated, fiveReset, weekReset;
    string error = "正在读取 Codex 用量";
    float scale = 1;
    object lastLayer, lastStatus;
    IntPtr taskbarOwner, fullscreenCandidate;
    DateTime fullscreenSince, nextWindowCheck, nextRefresh, nextCreditRefresh, nextTaskbarCheck, nextLayerRefresh;
    bool checkingTaskbar, checkingApps, apiSlotVisible;
    CancellationTokenSource usageRequest, creditRequest;
    int? startButtonLeft;
    int requiredBalanceWidth;
    int hideCount;
    bool codexOpen, claudeCodeOpen;
    bool intentionalExit, closing, idleExit, settingsPending, restorePending, useSavedOffset;
    DateTime? idleSince;
    string hiddenReason;
    readonly bool verifyMode = Array.IndexOf(Environment.GetCommandLineArgs(), "--verify") >= 0;
    readonly bool supervised = Array.IndexOf(Environment.GetCommandLineArgs(), "--supervised") >= 0;

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80000; if (!verifyMode) p.ExStyle |= 0x08000000 | 0x80; return p; }
    }

    public UsageWidget() : this(Program.DataDirectory) { }
    internal UsageWidget(string store)
    {
        this.store = store;
        Directory.CreateDirectory(store);
        Text = WidgetTitle;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = verifyMode;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        DoubleBuffered = true;
        try {
            var saved = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(store, "settings.json")));
            offset = Convert.ToInt32(saved["offset"]);
        } catch { useSavedOffset = true; }
        apiCredit.Load(store);
        if (supervised && Array.IndexOf(Environment.GetCommandLineArgs(), "--resume") >= 0)
            restorePending = !TryRestorePausedUsage();
        var menu = new ContextMenuStrip();
        menu.Items.Add("立即刷新", null, delegate { RefreshAll(); });
        menu.Items.Add("恢复左侧位置", null, delegate { offset = 152; SaveSettings(); PositionWidget(); });
        menu.Items.Add(new ToolStripSeparator());
        var startup = new ToolStripMenuItem("登录 Windows 后在后台运行");
        startup.CheckOnClick = true;
        menu.Opening += delegate { startup.Checked = Program.StartupEnabled(); };
        startup.Click += delegate {
            try {
                Program.SetStartup(startup.Checked);
            } catch (Exception ex) { Program.Log("startup setting failed: " + ex.Message); startup.Checked = Program.StartupEnabled(); }
        };
        menu.Items.Add(startup);
        menu.Items.Add("退出", null, delegate { intentionalExit = true; Program.ExitSignal.Set(); Close(); });
        ContextMenuStrip = menu;
        MouseDown += DragDown; MouseMove += DragMove; MouseUp += DragUp;
        DoubleClick += delegate { RefreshAll(); };
        locationTimer.Interval = 250;
        locationTimer.Tick += delegate { UpdateWidget(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            closing = true;
            locationTimer.Stop();
            if (e.CloseReason == CloseReason.WindowsShutDown) intentionalExit = true;
            if (usageRequest != null) usageRequest.Cancel();
            if (creditRequest != null) creditRequest.Cancel();
            if (e.CloseReason != CloseReason.WindowsShutDown && (usageRequest != null || creditRequest != null)) {
                e.Cancel = true;
                return; // FinishRefresh closes the form after both queries release their resources.
            }
            Program.Log("form closing reason=" + e.CloseReason + " intentional=" + intentionalExit);
        };
        FormClosed += delegate {
            Environment.ExitCode = intentionalExit || Program.ExitSignal.WaitOne(0) ? 0 : (idleExit ? Program.IdleExitCode : 75);
            locationTimer.Dispose(); tip.Dispose();
            if (claudeIcon != null) claudeIcon.Dispose();
            if (codexIcon != null) codexIcon.Dispose();
        };
        // Start hidden. The timer can close this form or show it when Codex opens.
        CreateHandle();
        locationTimer.Start();
    }

    void UpdateWidget()
    {
        if (Program.ExitSignal.WaitOne(0) || File.Exists(Path.Combine(store, "exit.request"))) {
            intentionalExit = true;
            Program.ExitSignal.Set();
            Close();
            return;
        }

        if (restorePending && TryRestorePausedUsage()) {
            restorePending = false;
            RenderUsage();
        }
        var now = DateTime.UtcNow;
        if (!checkingApps && now >= nextWindowCheck) {
            nextWindowCheck = now.AddSeconds(1);
            CheckRunningApps();
        }
        PositionWidget();
        if (codexOpen && now >= nextRefresh) RefreshUsage();
        if (claudeCodeOpen && now >= nextCreditRefresh) RefreshCredit();
    }

    async void CheckRunningApps()
    {
        checkingApps = true;
        try {
            // Reading a process's window or executable can stall during app startup or exit.
            Clients running = await Task.Run(() => ClientProcesses.Read());
            if (IsDisposed || closing) return;
            SetRunningApps((running & Clients.Codex) != 0, (running & Clients.ClaudeCode) != 0);
            if (supervised) CheckIdleExit(running);
        } catch (System.ComponentModel.Win32Exception) { idleSince = null; }
        finally { checkingApps = false; }
    }

    void CheckIdleExit(Clients running)
    {
        if (running != Clients.None) { idleSince = null; return; }
        // Keep quick close/reopen cycles in this worker. Release it only after cleanup
        // and a durable handoff, so a locked cache cannot lose the last reading.
        var now = DateTime.UtcNow;
        if (!idleSince.HasValue) idleSince = now;
        if ((now - idleSince.Value).TotalSeconds < 5 || usageRequest != null || creditRequest != null) return;
        if (!Program.HasSupervisor()) return;
        if (settingsPending) SaveSettings();
        if (settingsPending || !apiCredit.FlushCache(store) || !WriteStatus(true)) return;
        idleExit = true;
        Close();
    }

    void SetRunningApps(bool codexRunning, bool claudeRunning)
    {
        if (codexRunning == codexOpen && claudeRunning == claudeCodeOpen) return;
        if (codexRunning != codexOpen) {
            codexOpen = codexRunning;
            stale = true;
            error = codexOpen ? "正在读取 Codex 用量" : "Codex 未运行，已暂停查询。";
            if (codexOpen) nextRefresh = DateTime.UtcNow;
            else if (usageRequest != null) usageRequest.Cancel();
        }
        if (claudeRunning != claudeCodeOpen) {
            claudeCodeOpen = claudeRunning;
            nextCreditRefresh = claudeCodeOpen ? DateTime.UtcNow : DateTime.MaxValue;
            if (!claudeCodeOpen && creditRequest != null) creditRequest.Cancel();
        }
        RenderUsage();
    }

    void RefreshAll() { RefreshUsage(); RefreshCredit(); }

    void FinishRefresh()
    {
        if (IsDisposed) return;
        if (!closing) RenderUsage();
        else if (usageRequest == null && creditRequest == null) Close();
    }

    async void RefreshCredit()
    {
        if (!claudeCodeOpen || creditRequest != null || IsDisposed || closing) return;
        var request = new CancellationTokenSource();
        creditRequest = request;
        nextCreditRefresh = DateTime.UtcNow.AddMinutes(5);
        try {
            decimal amount = await Task.Run(() => ApiCreditBalance.Read(request.Token));
            if (IsDisposed || !claudeCodeOpen || request.IsCancellationRequested) return;
            apiCredit.Update(amount, store);
        } catch (OperationCanceledException) {
            // No refresh result can replace the saved value after Claude Code closes.
        } catch (Exception ex) {
            if (IsDisposed || request.IsCancellationRequested) return;
            apiCredit.MarkStale(ex.Message);
        } finally {
            creditRequest = null;
            request.Dispose();
            FinishRefresh();
        }
    }

    void SaveSettings()
    {
        useSavedOffset = false;
        bool wasPending = settingsPending;
        settingsPending = !Program.TryWriteState(Path.Combine(store, "settings.json"), json.Serialize(new { offset = offset }));
        if (settingsPending && !wasPending)
            Program.Log("could not save settings.json");
    }
    void DragDown(object sender, MouseEventArgs e) {
        if (e.Button != MouseButtons.Left) return;
        dragging = true; dragMouse = Cursor.Position; dragOrigin = offset;
        ((Control)sender).Capture = true;
    }
    void DragMove(object sender, MouseEventArgs e) {
        if (!dragging) return;
        offset = Math.Max(8, dragOrigin + (int)((Cursor.Position.X - dragMouse.X) / scale));
        PositionWidget();
    }
    void DragUp(object sender, MouseEventArgs e) {
        if (!dragging) return;
        dragging = false; ((Control)sender).Capture = false; SaveSettings();
    }
    void PositionWidget()
    {
        if (!codexOpen && !claudeCodeOpen) { HideWidget("Codex and Claude Code closed"); return; }
        var taskbar = FindWindow("Shell_TrayWnd", null);
        RECT r;
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out r)) { HideWidget("taskbar unavailable"); return; }
        // An owned window stays above the taskbar when Explorer raises it on a click.
        // Ownership does not inject code into Explorer or change its child windows.
        if (taskbarOwner != taskbar) {
            SetWindowLongPtr(Handle, -8, taskbar);
            taskbarOwner = taskbar;
            lastLayer = null;
            startButtonLeft = null;
            nextTaskbarCheck = DateTime.MinValue;
        }
        if (DateTime.UtcNow >= nextTaskbarCheck) CheckStartButton(taskbar);
        var screen = Screen.FromHandle(taskbar).Bounds;
        RECT f;
        var fg = GetForegroundWindow();
        var fgClass = new System.Text.StringBuilder(256);
        GetClassName(fg, fgClass, fgClass.Capacity);
        bool desktop = fgClass.ToString() == "Progman" || fgClass.ToString() == "WorkerW";
        bool fullscreen = fg != Handle && fg != taskbar && !IsZoomed(fg) && GetWindowRect(fg, out f)
            && !desktop && f.Left <= screen.Left && f.Top <= screen.Top && f.Right >= screen.Right && f.Bottom >= screen.Bottom;
        // Window switching and maximize animations must not briefly hide the row.
        if (fullscreen) {
            if (fullscreenCandidate != fg) { fullscreenCandidate = fg; fullscreenSince = DateTime.UtcNow; }
            fullscreen = (DateTime.UtcNow - fullscreenSince).TotalMilliseconds >= 1200;
        } else fullscreenCandidate = IntPtr.Zero;
        bool taskbarVisible = IsWindowVisible(taskbar) && r.Top < screen.Bottom - 4 && r.Bottom > screen.Top + 4;
        if (!taskbarVisible || fullscreen) { HideWidget(fullscreen ? "stable fullscreen" : "taskbar hidden"); return; }
        uint dpi = 96;
        try { dpi = GetDpiForWindow(taskbar); } catch { }
        float nextScale = dpi / 96f;
        if (nextScale < 1) nextScale = 1;
        scale = nextScale;
        int baseWidth = (int)((WeeklyOnly ? 299 : 332) * scale);
        int height = Math.Min(r.Bottom - r.Top - 4, (int)(38 * scale));
        if (height < 20 || r.Right - r.Left < baseWidth) { HideWidget("taskbar too small"); return; }
        int left = r.Left + 8;
        int x = Math.Max(left, Math.Min(r.Left + (int)(offset * scale), r.Right - baseWidth - 8));
        int right = Math.Min(r.Right - 8, startButtonLeft.GetValueOrDefault(r.Right) - (int)(9 * scale));
        // With less space before Start, keep both readouts by moving the row only as far as needed.
        if (right - x < requiredBalanceWidth && right - left >= requiredBalanceWidth)
            x = right - requiredBalanceWidth;
        int preferredWidth = Math.Max(baseWidth + (int)(98 * scale), requiredBalanceWidth);
        int width = Math.Max(baseWidth, Math.Min(preferredWidth, right - x));
        int y = r.Top + (r.Bottom - r.Top - height) / 2;
        if (!Visible) { lastLayer = null; Show(); }
        if (hiddenReason != null) Program.Log("taskbar widget restored after " + hiddenReason);
        hiddenReason = null;
        uint positioningFlags = 0x0010;
        if (!IsWindowVisible(Handle)) { lastLayer = null; positioningFlags |= 0x0040; }
        if (Left == x && Top == y) positioningFlags |= 0x0002;
        if (Width == width && Height == height) positioningFlags |= 0x0001;
        SetWindowPos(Handle, new IntPtr(-1), x, y, width, height, positioningFlags);
        DrawLayer();
        WriteStatus();
    }
    async void CheckStartButton(IntPtr taskbar)
    {
        if (checkingTaskbar) return;
        checkingTaskbar = true;
        nextTaskbarCheck = DateTime.UtcNow.AddSeconds(8);
        try {
            var left = await Task.Run(() => FindStartButtonLeft(taskbar));
            if (!IsDisposed && taskbarOwner == taskbar) startButtonLeft = left;
        } catch { }
        finally { checkingTaskbar = false; }
    }
    internal static int? FindStartButtonLeft(IntPtr taskbar)
    {
        var root = AutomationElement.FromHandle(taskbar);
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"),
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "开始"),
                    new PropertyCondition(AutomationElement.NameProperty, "Start"))));
        var start = root.FindFirst(TreeScope.Descendants, condition);
        if (start == null) return null;
        var rect = start.Current.BoundingRectangle;
        return rect.IsEmpty || rect.Width <= 0 ? (int?)null : (int)rect.Left;
    }
    void HideWidget(string reason)
    {
        if (Visible) { Hide(); hideCount++; lastLayer = null; }
        if (hiddenReason != reason) Program.Log("taskbar widget hidden: " + reason);
        hiddenReason = reason;
        WriteStatus();
    }
    void DrawLayer()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        var signature = new { Bounds, scale, fiveLeft, weekLeft, stale, fiveReset, weekReset, WeeklyOnly,
            apiCredit.Amount, claudeCodeOpen, startButtonLeft };
        if (signature.Equals(lastLayer) && DateTime.UtcNow < nextLayerRefresh) return;
        using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bitmap)) {
                // An almost invisible alpha gives the entire row a usable mouse target.
                // The real taskbar remains visible behind it, including its wallpaper tint.
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var font = new Font("Microsoft YaHei UI", (WeeklyOnly ? 13f : 12.7f) * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var resetFont = new Font("Microsoft YaHei UI", (WeeklyOnly ? 11.5f : 9.5f) * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var weeklyCaptionFont = new Font("Microsoft YaHei UI", (WeeklyOnly ? 11.5f : 12.7f) * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var headingFont = new Font("华文琥珀", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var headingBrush = new SolidBrush(Color.FromArgb(246, 178, 196)))
                using (var captionBrush = new SolidBrush(Color.FromArgb(187, 190, 198)))
                using (var resetBrush = new SolidBrush(Color.FromArgb(177, 180, 188)))
                using (var format = new StringFormat(StringFormat.GenericTypographic))
                using (var separator = new Pen(Color.FromArgb(55, 220, 225, 235))) {
                    format.LineAlignment = StringAlignment.Near;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    float titleTop = (Height - font.GetHeight(g)) / 2f;
                    float lineGap = 1.5f * scale;
                    float textTop = WeeklyOnly ? (Height - font.GetHeight(g)) / 2f : (Height - font.GetHeight(g) - resetFont.GetHeight(g) - lineGap) / 2f;
                    float resetTop = WeeklyOnly ? (Height - resetFont.GetHeight(g)) / 2f : textTop + font.GetHeight(g) + lineGap;
                    float baseline = titleTop + font.Size * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);
                    if (WeeklyOnly) resetTop = baseline - resetFont.Size * resetFont.FontFamily.GetCellAscent(resetFont.Style) / resetFont.FontFamily.GetEmHeight(resetFont.Style);
                    float headingTop = baseline - headingFont.Size * headingFont.FontFamily.GetCellAscent(headingFont.Style) / headingFont.FontFamily.GetEmHeight(headingFont.Style);
                    DrawBrain(g);
                    float titleX = 31*scale;
                    g.DrawString(WidgetTitle, headingFont, headingBrush, new PointF(titleX, headingTop), format);
                    float firstSeparator = 115 * scale, secondSeparator = 220 * scale;
                    string weekValue = Percent(weekLeft) + (stale && weekLeft.HasValue ? "*" : "");
                    string weekResetLabel = ResetLabel(weekReset, true);
                    float iconSize = 15 * scale;
                    float iconCenterY = Height / 2f;
                    float codexX = (WeeklyOnly ? firstSeparator : secondSeparator) + 14 * scale;
                    float weekX = codexX + iconSize + 7 * scale;
                    float valueX = weekX + g.MeasureString("每周", weeklyCaptionFont, int.MaxValue, format).Width + 10 * scale;
                    float valueRight = valueX + g.MeasureString(weekValue, font, int.MaxValue, format).Width;
                    float minResetX = WeeklyOnly ? valueRight + 8 * scale : weekX;
                    float amountWidth = g.MeasureString(apiCredit.Display, font, int.MaxValue, format).Width;
                    float starWidth = claudeCodeOpen ? 0 : scale + g.MeasureString("*", font, int.MaxValue, format).Width;
                    float apiWidth = iconSize + 6 * scale + amountWidth + starWidth;
                    float resetWidth = g.MeasureString(weekResetLabel, resetFont, int.MaxValue, format).Width;
                    float groupGap = 10 * scale;
                    float minimumApiX = Math.Max(minResetX + resetWidth, valueRight) + groupGap;
                    float rightPadding = 6 * scale;
                    requiredBalanceWidth = (int)Math.Ceiling(minimumApiX + apiWidth + rightPadding) + 1;
                    float idealApiX = Math.Max((WeeklyOnly ? 321 : 338) * scale, minimumApiX);
                    float apiX = Math.Min(idealApiX, Width - rightPadding - apiWidth);
                    float resetX = WeeklyOnly
                        ? Math.Max(minResetX, Math.Min(216 * scale, apiX - resetWidth - groupGap))
                        : weekX;
                    float mainRight = Math.Max(resetX + resetWidth, valueRight);
                    apiSlotVisible = apiX + 0.01f >= mainRight + groupGap;
                    g.DrawLine(separator, firstSeparator, Height/2f-7*scale, firstSeparator, Height/2f+7*scale);
                    if (!WeeklyOnly) {
                        g.DrawLine(separator, secondSeparator, Height/2f-7*scale, secondSeparator, Height/2f+7*scale);
                        g.DrawString("5小时", font, captionBrush, new PointF(127*scale, textTop), format);
                        using (var valueBrush = new SolidBrush(AmountColor(fiveLeft)))
                            g.DrawString(Percent(fiveLeft) + (stale && fiveLeft.HasValue ? "*" : ""), font, valueBrush, new PointF(173*scale, textTop), format);
                        g.DrawString(ResetLabel(fiveReset, false), resetFont, resetBrush, new PointF(127*scale, resetTop), format);
                    }
                    float captionTop = WeeklyOnly ? baseline - weeklyCaptionFont.Size * weeklyCaptionFont.FontFamily.GetCellAscent(weeklyCaptionFont.Style) / weeklyCaptionFont.FontFamily.GetEmHeight(weeklyCaptionFont.Style) : textTop;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    if (codexIcon == null) codexIcon = LoadIcon("Codex.png");
                    DrawIcon(g, codexIcon, codexX, iconCenterY, iconSize);
                    g.DrawString("每周", weeklyCaptionFont, captionBrush, new PointF(weekX, captionTop), format);
                    using (var valueBrush = new SolidBrush(AmountColor(weekLeft)))
                        g.DrawString(weekValue, font, valueBrush, new PointF(valueX, textTop), format);
                    g.DrawString(weekResetLabel, resetFont, resetBrush, new PointF(resetX, resetTop), format);
                    if (apiSlotVisible) {
                        if (claudeIcon == null) claudeIcon = LoadIcon("Claude.png");
                        DrawIcon(g, claudeIcon, apiX, iconCenterY, iconSize);
                        float amountX = apiX + iconSize + 6 * scale;
                        using (var apiBrush = new SolidBrush(ApiAmountColor(apiCredit.Amount))) {
                            g.DrawString(apiCredit.Display, font, apiBrush, new PointF(amountX, titleTop), format);
                            if (!claudeCodeOpen)
                                g.DrawString("*", font, apiBrush, new PointF(amountX + amountWidth + scale, titleTop - 2 * scale), format);
                        }
                    }
                }
            }
            IntPtr dc = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(dc);
            IntPtr dib = bitmap.GetHbitmap(Color.FromArgb(0)), previous = SelectObject(memory, dib);
            try {
                var position = new POINT(Left, Top); var size = new SIZE(Width, Height); var origin = new POINT(0,0);
                var blend = new BLEND { Operation=0, Flags=0, Alpha=255, Format=1 };
                if (UpdateLayeredWindow(Handle, dc, ref position, ref size, memory, ref origin, 0, ref blend, 2)) {
                    lastLayer = signature;
                    nextLayerRefresh = DateTime.UtcNow.AddSeconds(8);
                }
            } finally { SelectObject(memory, previous); DeleteObject(dib); DeleteDC(memory); ReleaseDC(IntPtr.Zero, dc); }
        }
    }
    static Bitmap LoadIcon(string resourceName)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        using (var icon = new Bitmap(stream)) {
            int left = icon.Width, right = -1, top = icon.Height, bottom = -1;
            for (int y = 0; y < icon.Height; y++) {
                for (int x = 0; x < icon.Width; x++) {
                    if (icon.GetPixel(x, y).A < 16) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            }
            if (right < left) return new Bitmap(icon);
            return icon.Clone(Rectangle.FromLTRB(left, top, right + 1, bottom + 1), PixelFormat.Format32bppArgb);
        }
    }
    static void DrawIcon(Graphics g, Bitmap icon, float left, float centerY, float size)
    {
        float ratio = size / Math.Max(icon.Width, icon.Height);
        float width = icon.Width * ratio, height = icon.Height * ratio;
        g.DrawImage(icon, new RectangleF(left + (size - width) / 2f, centerY - height / 2f, width, height));
    }
    void DrawBrain(Graphics g)
    {
        var saved = g.Save();
        try {
            g.TranslateTransform(8*scale, Height/2f-8*scale);
            g.ScaleTransform(0.85f*scale, 0.85f*scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = new GraphicsPath())
            using (var fill = new SolidBrush(Color.FromArgb(244, 165, 184)))
            using (var folds = new Pen(Color.FromArgb(195, 103, 132), 1.1f)) {
                folds.StartCap = folds.EndCap = LineCap.Round;
                shape.AddBezier(10,3, 7,0, 3,0, 2,5);
                shape.AddBezier(2,5, -1,6, 0,10, 2,11);
                shape.AddBezier(2,11, 0,15, 3,18, 6,16);
                shape.AddBezier(6,16, 8,19, 10,18, 10,15);
                shape.AddBezier(10,15, 10,18, 13,19, 14,16);
                shape.AddBezier(14,16, 18,18, 21,14, 18,11);
                shape.AddBezier(18,11, 21,9, 20,6, 18,5);
                shape.AddBezier(18,5, 17,0, 13,0, 10,3);
                shape.CloseFigure();
                g.FillPath(fill, shape);
                g.DrawPath(folds, shape);
                g.DrawBezier(folds, 10,3, 9,7, 11,11, 10,15);
                g.DrawBezier(folds, 3,6, 7,4, 8,6, 6,9);
                g.DrawBezier(folds, 2,11, 5,9, 7,11, 6,14);
                g.DrawBezier(folds, 17,6, 13,4, 12,6, 14,9);
                g.DrawBezier(folds, 18,11, 15,9, 13,11, 14,14);
            }
        } finally { g.Restore(saved); }
    }
    static readonly Color NoAmount = Color.FromArgb(153, 156, 164);
    static readonly Color LowAmount = Color.FromArgb(255, 116, 119);
    static readonly Color MediumAmount = Color.FromArgb(255, 209, 102);
    static readonly Color HighAmount = Color.FromArgb(125, 221, 183);

    static Color AmountColor(double? value) {
        if (!value.HasValue) return NoAmount;
        if (value.Value <= 100.0 / 3.0) return LowAmount;
        if (value.Value <= 200.0 / 3.0) return MediumAmount;
        return HighAmount;
    }
    internal static Color ApiAmountColor(decimal? amount) {
        if (!amount.HasValue) return NoAmount;
        if (amount.Value < 34m) return LowAmount;
        if (amount.Value < 67m) return MediumAmount;
        return HighAmount;
    }
    static string Percent(double? v) { return v.HasValue ? v.Value.ToString("0.#") + "%" : "--"; }
    static readonly string[] Weekdays = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
    static string ResetLabel(DateTime? reset, bool weekly)
    {
        if (!reset.HasValue) return "重置时间 --";
        return (weekly ? Weekdays[(int)reset.Value.DayOfWeek] + " " : "") + reset.Value.ToString("HH:mm") + " 重置";
    }
    void RenderUsage()
    {
        string detail = "外置大脑剩余额度（每 60 秒自动刷新）";
        if (!WeeklyOnly) detail += "\n5 小时：" + Percent(fiveLeft);
        detail += "\n每周：" + Percent(weekLeft);
        if (fiveReset.HasValue) detail += "\n5 小时重置：" + fiveReset.Value.ToString("MM-dd HH:mm");
        if (weekReset.HasValue) detail += "\n每周重置：" + weekReset.Value.ToString("MM-dd HH:mm");
        if (lastUpdated.HasValue) detail += "\n更新：" + lastUpdated.Value.ToString("HH:mm:ss");
        if (!codexOpen) detail += "\nCodex 未运行，已暂停查询。";
        else if (stale) detail += "\n尚未更新，带 * 的数字为上次成功读取值。\n" + error;
        detail += "\nOpenAI API 余额：" + apiCredit.Display;
        if (apiCredit.LastRead.HasValue) detail += " · " + apiCredit.LastRead.Value.ToLocalTime().ToString("MM-dd HH:mm:ss");
        if (!claudeCodeOpen) detail += "\n* Claude Code 未运行，显示上次余额，已暂停查询。";
        else if (apiCredit.Stale) detail += "\n" + apiCredit.Note;
        else detail += "\n来自 OpenAI Platform，每 5 分钟更新";
        detail += "\n双击刷新 · 拖动调整位置 · 右键菜单";
        tip.SetToolTip(this, detail);
        PositionWidget();
    }
    internal bool WriteStatus(bool force = false)
    {
        if (restorePending) return false;
        try {
            var status = new {
                fiveHourRemaining = fiveLeft, weeklyRemaining = weekLeft, hasFiveHour = hasFive, hasWeekly = hasWeek, stale = stale,
                fiveHourReset = fiveReset.HasValue ? fiveReset.Value.ToString("o") : null,
                weeklyReset = weekReset.HasValue ? weekReset.Value.ToString("o") : null,
                fiveHourResetLabel = ResetLabel(fiveReset, false), weeklyResetLabel = ResetLabel(weekReset, true),
                updated = lastUpdated.HasValue ? lastUpdated.Value.ToString("o") : null,
                apiCredit = apiCredit.Display, apiCreditUpdated = apiCredit.LastRead, apiCreditStale = apiCredit.Stale,
                apiSlotVisible = apiSlotVisible, requiredBalanceWidth = requiredBalanceWidth, startButtonLeft = startButtonLeft,
                claudeCodeOpen = claudeCodeOpen, apiCreditPaused = !claudeCodeOpen, apiQueryRunning = creditRequest != null,
                apiCreditError = claudeCodeOpen && apiCredit.Stale ? apiCredit.Note : null,
                codexOpen = codexOpen, visible = Visible, x = Left, y = Top, width = Width, height = Height, scale = scale, offset = offset,
                hideCount = hideCount, hiddenReason = hiddenReason, taskbarOwned = taskbarOwner != IntPtr.Zero,
                job = Program.JobState,
                error = stale ? error : null
            };
            if (!force && status.Equals(lastStatus)) return true;
            if (!Program.TryWriteState(Path.Combine(store, "status.json"), json.Serialize(status))) return false;
            lastStatus = status;
            return true;
        } catch { }
        return false;
    }
    static DateTime? StoredDate(Dictionary<string, object> record, string field)
    {
        string value = Field(record, field) as string;
        return value == null ? (DateTime?)null : DateTime.ParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
    internal bool TryRestorePausedUsage()
    {
        try {
            var saved = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(store, "status.json")));
            object savedOffset = Field(saved, "offset");
            if (useSavedOffset && savedOffset is int) { offset = (int)savedOffset; useSavedOffset = false; }
            object count = Field(saved, "hideCount");
            if (count is int) hideCount = Math.Max(hideCount, (int)count);
            DateTime? balanceRead = Field(saved, "apiCreditUpdated") as DateTime?;
            decimal balance;
            if (!apiCredit.Amount.HasValue && balanceRead.HasValue && ApiCreditBalance.TryParseAmount(Field(saved, "apiCredit") as string, out balance))
                apiCredit.Restore(balance, balanceRead.Value);
            // A fresh quota request may have finished while the old file was locked.
            if (lastUpdated.HasValue) return true;
            DateTime? updated = StoredDate(saved, "updated");
            if (!updated.HasValue) return true;
            double? five = NumberField(saved, "fiveHourRemaining"), week = NumberField(saved, "weeklyRemaining");
            DateTime? shortReset = StoredDate(saved, "fiveHourReset"), longReset = StoredDate(saved, "weeklyReset");
            hasFive = Equals(Field(saved, "hasFiveHour"), true);
            hasWeek = Equals(Field(saved, "hasWeekly"), true);
            fiveLeft = five; weekLeft = week;
            fiveReset = shortReset; weekReset = longReset;
            lastUpdated = updated;
            stale = true;
            if (!codexOpen) error = "Codex 未运行，已暂停查询。";
        } catch (FileNotFoundException) { }
          catch (IOException) { return false; }
          catch (UnauthorizedAccessException) { return false; }
          catch (Exception ex) { Program.Log("invalid paused usage: " + ex.Message); }
        return true;
    }
    static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
    static object Field(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v : null; }
    static double? NumberField(Dictionary<string, object> window, string key)
    {
        object value = Field(window, key);
        if (value == null) return null;
        double number;
        if (Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number)) return number;
        throw new FormatException("Codex 额度字段 " + key + " 不是有效数字。");
    }
    internal static double? Remaining(Dictionary<string, object> window) {
        double? used = NumberField(window, "usedPercent");
        return used.HasValue ? Math.Max(0, Math.Min(100, 100 - used.Value)) : (double?)null;
    }
    static DateTime? Reset(Dictionary<string, object> window) {
        double? unix = NumberField(window, "resetsAt");
        return unix.HasValue ? new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(unix.Value).ToLocalTime() : (DateTime?)null;
    }
    internal static void SelectWindows(Dictionary<string, object> limits, out Dictionary<string, object> fiveWindow, out Dictionary<string, object> weekWindow)
    {
        fiveWindow = null; weekWindow = null;
        foreach (var window in new[] { Obj(Field(limits, "primary")), Obj(Field(limits, "secondary")) }) {
            var duration = Field(window, "windowDurationMins");
            if (duration == null) continue;
            double minutes;
            if (!Double.TryParse(Convert.ToString(duration, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)) continue;
            if (minutes == 300 && fiveWindow == null) fiveWindow = window;
            if (minutes == 10080 && weekWindow == null) weekWindow = window;
        }
        if (fiveWindow == null && weekWindow == null)
            throw new Exception("账号未提供可识别的 5 小时或每周额度窗口。");
    }
    static string FindCodex()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root)) {
            var files = Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories);
            if (files.Length > 0) { Array.Sort(files, (a,b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a))); return files[0]; }
        }
        throw new Exception("未找到本机 Codex，打开 Codex 并确认已登录后重试。");
    }
    static async Task<Dictionary<string, object>> ReadReply(StreamReader output, JavaScriptSerializer serializer,
        string expectedId, Task deadline, CancellationToken cancellation)
    {
        while (!deadline.IsCompleted) {
            var reading = output.ReadLineAsync();
            if (await Task.WhenAny(deadline, reading).ConfigureAwait(false) == deadline) break;
            string line = await reading.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (line == null) throw new EndOfStreamException("Codex 用量接口已关闭。");
            var packet = serializer.Deserialize<Dictionary<string, object>>(line);
            if (Convert.ToString(Field(packet, "id"), CultureInfo.InvariantCulture) == expectedId) return packet;
        }
        cancellation.ThrowIfCancellationRequested();
        throw new TimeoutException("读取超时，稍后自动重试。");
    }
    internal static async Task<Dictionary<string, object>> FetchUsage(string executable, string directory,
        CancellationToken cancellation, int timeoutMilliseconds = 25000)
    {
        cancellation.ThrowIfCancellationRequested();
        using (var p = new Process())
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
            p.StartInfo = new ProcessStartInfo(executable, "app-server") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, WorkingDirectory = directory
            };
            var serializer = new JavaScriptSerializer();
            p.Start();
            try {
                using (cancellation.Register(() => Program.KillProcess(p))) {
                    var deadline = Task.Delay(timeoutMilliseconds, stop.Token);
                    p.BeginErrorReadLine();
                    p.StandardInput.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex_taskbar_usage\",\"title\":\"Codex Taskbar Usage\",\"version\":\"1.0\"}}}");
                    var init = await ReadReply(p.StandardOutput, serializer, "1", deadline, cancellation).ConfigureAwait(false);
                    if (Field(init, "error") != null) throw new Exception("Codex 用量接口初始化失败。");
                    p.StandardInput.WriteLine("{\"method\":\"initialized\",\"params\":{}}");
                    p.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
                    var packet = await ReadReply(p.StandardOutput, serializer, "2", deadline, cancellation).ConfigureAwait(false);
                    if (Field(packet, "error") != null) throw new Exception("无法读取用量，请确认 Codex 已登录。");
                    var result = Obj(Field(packet, "result"));
                    var buckets = Obj(Field(result, "rateLimitsByLimitId"));
                    var limits = Obj(Field(buckets, "codex")) ?? Obj(Field(result, "rateLimits"));
                    if (limits == null) throw new Exception("账号未提供 Codex 额度信息。");
                    return limits;
                }
            } catch {
                cancellation.ThrowIfCancellationRequested();
                throw;
            } finally {
                stop.Cancel();
                Program.KillProcess(p);
                p.WaitForExit(2000);
            }
        }
    }
    async void RefreshUsage()
    {
        if (!codexOpen || usageRequest != null || IsDisposed || closing) return;
        var request = new CancellationTokenSource();
        usageRequest = request;
        nextRefresh = DateTime.UtcNow.AddMinutes(1);
        try {
            var limits = await Task.Run(() => FetchUsage(FindCodex(), store, request.Token));
            if (IsDisposed || request.IsCancellationRequested) return;
            Dictionary<string, object> fiveWindow, weekWindow;
            SelectWindows(limits, out fiveWindow, out weekWindow);
            // Complete conversion before updating the current display; a malformed response
            // must not leave a mixture of old and new data on screen.
            double? nextFive = Remaining(fiveWindow), nextWeek = Remaining(weekWindow);
            DateTime? nextFiveReset = Reset(fiveWindow), nextWeekReset = Reset(weekWindow);
            hasFive = fiveWindow != null; hasWeek = weekWindow != null;
            fiveLeft = nextFive; weekLeft = nextWeek;
            fiveReset = nextFiveReset; weekReset = nextWeekReset;
            lastUpdated = DateTime.Now; stale = false; error = null;
        } catch (Exception ex) {
            if (IsDisposed || request.IsCancellationRequested) return;
            stale = true;
            error = ex.Message;
        } finally {
            usageRequest = null;
            request.Dispose();
            FinishRefresh();
        }
    }
}
