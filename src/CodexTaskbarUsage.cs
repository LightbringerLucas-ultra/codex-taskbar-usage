using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job, int info, IntPtr buffer, uint size, out uint returned);
    const string TaskName = "外置大脑剩余";
    internal static readonly string DataDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "..", "work", "runtime"));
    internal static readonly string JobState = ReadJobState();
    internal static readonly EventWaitHandle ExitSignal = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\CodexTaskbarUsage_Exit_JYH");
    static readonly string Store = DataDirectory;
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
        if (Array.IndexOf(args, "--worker") < 0 && Array.IndexOf(args, "--verify") < 0) { Supervise(); return; }
        bool created;
        using (var mutex = new Mutex(true, "Local\\CodexTaskbarUsage_JYH", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Log("worker running pid=" + Process.GetCurrentProcess().Id + " job=" + JobState);
            try { Application.Run(new UsageWidget()); }
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
        using (var mutex = new Mutex(true, "Local\\CodexTaskbarUsage_Supervisor_JYH", out created)) {
            if (!created) return;
            ExitSignal.Reset();
            Directory.CreateDirectory(Store);
            if (File.Exists(Path.Combine(Store, "exit.request"))) File.Delete(Path.Combine(Store, "exit.request"));
            int restartCount = 0, quickFailures = 0;
            Log("supervisor started pid=" + Process.GetCurrentProcess().Id + " job=" + JobState);
            while (!ExitSignal.WaitOne(0)) {
                DateTime started = DateTime.UtcNow;
                try {
                    using (var child = ExistingWorker() ?? Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--worker") {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath)
                    })) {
                        File.WriteAllText(Path.Combine(Store, "watchdog.json"), new JavaScriptSerializer().Serialize(new {
                            supervisorPid = Process.GetCurrentProcess().Id, workerPid = child.Id,
                            restartCount = restartCount, started = DateTime.Now.ToString("o"), job = JobState
                        }));
                        Log("worker started pid=" + child.Id + " restartCount=" + restartCount);
                        child.WaitForExit();
                        Log("worker exited code=" + child.ExitCode);
                        if (child.ExitCode == 0 || ExitSignal.WaitOne(0)) break;
                    }
                } catch (Exception ex) { Log("supervisor exception: " + ex.Message); }
                restartCount++;
                quickFailures = (DateTime.UtcNow - started).TotalSeconds < 10 ? quickFailures + 1 : 0;
                if (ExitSignal.WaitOne(Math.Min(30000, 1000 * (1 << Math.Min(quickFailures, 5))))) break;
            }
            Log("supervisor stopped");
        }
    }
    static Process ExistingWorker()
    {
        Process candidate = null;
        try {
            var previous = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Store, "watchdog.json")));
            candidate = Process.GetProcessById(Convert.ToInt32(previous["workerPid"]));
            if (candidate.Id != Process.GetCurrentProcess().Id && !candidate.HasExited &&
                String.Equals(candidate.MainModule.FileName, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) {
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
    delegate void WinEventCallback(IntPtr hook, uint ev, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
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

    readonly string store = Program.DataDirectory;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly Label title = new Label();
    readonly Label five = new Label();
    readonly Label week = new Label();
    readonly ToolTip tip = new ToolTip();
    readonly System.Windows.Forms.Timer locationTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
    bool busy, stale = true, dragging;
    int offset = 152;
    int dragOrigin;
    Point dragMouse;
    double? fiveLeft, weekLeft;
    DateTime? lastUpdated, fiveReset, weekReset;
    string error = "正在读取 Codex 用量";
    float scale = 1;
    string lastLayer;
    IntPtr taskbarOwner, foregroundHook, fullscreenCandidate;
    WinEventCallback foregroundCallback;
    DateTime fullscreenSince;
    int hideCount;
    bool positionQueued;
    bool intentionalExit;
    string hiddenReason;
    readonly bool verifyMode = Array.IndexOf(Environment.GetCommandLineArgs(), "--verify") >= 0;

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80000; if (!verifyMode) p.ExStyle |= 0x08000000 | 0x80; return p; }
    }

    public UsageWidget()
    {
        Directory.CreateDirectory(store);
        Text = "外置大脑剩余";
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
        } catch { }
        foreach (var l in new[] { title, five, week }) {
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.BackColor = Color.Transparent;
            l.MouseDown += DragDown;
            l.MouseMove += DragMove;
            l.MouseUp += DragUp;
            l.DoubleClick += delegate { RefreshUsage(); };
        }
        title.Text = "外置大脑剩余";
        title.ForeColor = Color.FromArgb(205, 207, 214);
        five.Text = "5h  --";
        week.Text = "每周  --";
        var menu = new ContextMenuStrip();
        menu.Items.Add("立即刷新", null, delegate { RefreshUsage(); });
        menu.Items.Add("恢复左侧位置", null, delegate { offset = 152; SaveSettings(); PositionWidget(); });
        menu.Items.Add(new ToolStripSeparator());
        var startup = new ToolStripMenuItem("登录 Windows 后自动显示");
        startup.CheckOnClick = true;
        startup.Checked = Program.StartupEnabled();
        startup.Click += delegate {
            try {
                Program.SetStartup(startup.Checked);
            } catch (Exception ex) { Program.Log("startup setting failed: " + ex.Message); startup.Checked = Program.StartupEnabled(); }
        };
        menu.Items.Add(startup);
        menu.Items.Add("退出", null, delegate { intentionalExit = true; Program.ExitSignal.Set(); Close(); });
        ContextMenuStrip = menu;
        foreach (var l in new[] { title, five, week }) l.ContextMenuStrip = menu;
        MouseDown += DragDown; MouseMove += DragMove; MouseUp += DragUp;
        DoubleClick += delegate { RefreshUsage(); };
        locationTimer.Interval = 250;
        locationTimer.Tick += delegate {
            if (Program.ExitSignal.WaitOne(0) || File.Exists(Path.Combine(store, "exit.request"))) {
                intentionalExit = true; Program.ExitSignal.Set(); Close();
            }
            else PositionWidget();
        };
        refreshTimer.Interval = 60000;
        refreshTimer.Tick += delegate { RefreshUsage(); };
        Shown += delegate {
            foregroundCallback = delegate {
                if (IsDisposed || positionQueued) return;
                positionQueued = true;
                try { BeginInvoke((Action)delegate { positionQueued = false; if (!IsDisposed) PositionWidget(); }); }
                catch { positionQueued = false; }
            };
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 0);
            PositionWidget(); locationTimer.Start(); refreshTimer.Start(); RefreshUsage();
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (e.CloseReason == CloseReason.WindowsShutDown) intentionalExit = true;
            Program.Log("form closing reason=" + e.CloseReason + " intentional=" + intentionalExit);
        };
        FormClosed += delegate {
            Environment.ExitCode = intentionalExit || Program.ExitSignal.WaitOne(0) ? 0 : 75;
            if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
            locationTimer.Dispose(); refreshTimer.Dispose(); tip.Dispose();
        };
    }

    void SaveSettings() { File.WriteAllText(Path.Combine(store, "settings.json"), json.Serialize(new { offset = offset })); }
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
        var taskbar = FindWindow("Shell_TrayWnd", null);
        RECT r;
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out r)) { HideWidget("taskbar unavailable"); return; }
        // An owned window stays above the taskbar when Explorer raises it on a click.
        // Ownership does not inject code into Explorer or change its child windows.
        if (taskbarOwner != taskbar) {
            SetWindowLongPtr(Handle, -8, taskbar);
            taskbarOwner = taskbar;
        }
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
        int width = (int)(316 * scale), height = Math.Min(r.Bottom - r.Top - 4, (int)(38 * scale));
        if (height < 20 || r.Right - r.Left < width) { HideWidget("taskbar too small"); return; }
        int x = Math.Max(r.Left + 8, Math.Min(r.Left + (int)(offset * scale), r.Right - width - 8));
        int y = r.Top + (r.Bottom - r.Top - height) / 2;
        if (!Visible) { lastLayer = null; Show(); }
        hiddenReason = null;
        uint positioningFlags = 0x0010;
        if (Left == x && Top == y) positioningFlags |= 0x0002;
        if (Width == width && Height == height) positioningFlags |= 0x0001;
        SetWindowPos(Handle, new IntPtr(-1), x, y, width, height, positioningFlags);
        LayoutLabels();
        DrawLayer();
        WriteStatus();
    }
    void HideWidget(string reason)
    {
        if (Visible) { Hide(); hideCount++; lastLayer = null; }
        hiddenReason = reason;
        WriteStatus();
    }
    void LayoutLabels()
    {
        int h = ClientSize.Height;
        title.SetBounds((int)(10*scale), 0, (int)(98*scale), h);
        five.SetBounds((int)(126*scale), 0, (int)(80*scale), h);
        week.SetBounds((int)(232*scale), 0, (int)(80*scale), h);
        float size = 9.5f;
        if (title.Font.Size != size) {
            foreach (var l in new[] { title, five, week }) {
                var old = l.Font;
                l.Font = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Point);
                if (old != Control.DefaultFont) old.Dispose();
            }
        }
    }
    void DrawLayer()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        string signature = Width + ":" + Height + ":" + Left + ":" + Top + ":" + five.Text + ":" + week.Text + ":" + stale
            + ":" + ResetLabel(fiveReset, false) + ":" + ResetLabel(weekReset, true);
        if (lastLayer == signature) return;
        using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bitmap)) {
                // An almost invisible alpha gives the entire row a usable mouse target.
                // The real taskbar remains visible behind it, including its wallpaper tint.
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var font = new Font("Microsoft YaHei UI", 12.7f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var resetFont = new Font("Microsoft YaHei UI", 9.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
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
                    float textTop = (Height - font.GetHeight(g) - resetFont.GetHeight(g) - lineGap) / 2f;
                    float resetTop = textTop + font.GetHeight(g) + lineGap;
                    float baseline = titleTop + font.Size * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);
                    float headingTop = baseline - headingFont.Size * headingFont.FontFamily.GetCellAscent(headingFont.Style) / headingFont.FontFamily.GetEmHeight(headingFont.Style);
                    DrawBrain(g);
                    g.DrawString(title.Text, headingFont, headingBrush, new PointF(31*scale, headingTop), format);
                    g.DrawLine(separator, 115*scale, Height/2f-7*scale, 115*scale, Height/2f+7*scale);
                    g.DrawLine(separator, 220*scale, Height/2f-7*scale, 220*scale, Height/2f+7*scale);
                    g.DrawString("5小时", font, captionBrush, new PointF(127*scale, textTop), format);
                    g.DrawString("每周", font, captionBrush, new PointF(232*scale, textTop), format);
                    using (var valueBrush = new SolidBrush(AmountColor(fiveLeft, stale)))
                        g.DrawString(Percent(fiveLeft) + (stale && fiveLeft.HasValue ? "*" : ""), font, valueBrush, new PointF(173*scale, textTop), format);
                    using (var valueBrush = new SolidBrush(AmountColor(weekLeft, stale)))
                        g.DrawString(Percent(weekLeft) + (stale && weekLeft.HasValue ? "*" : ""), font, valueBrush, new PointF(270*scale, textTop), format);
                    g.DrawString(ResetLabel(fiveReset, false), resetFont, resetBrush, new PointF(127*scale, resetTop), format);
                    g.DrawString(ResetLabel(weekReset, true), resetFont, resetBrush, new PointF(232*scale, resetTop), format);
                }
            }
            IntPtr dc = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(dc);
            IntPtr dib = bitmap.GetHbitmap(Color.FromArgb(0)), previous = SelectObject(memory, dib);
            try {
                var position = new POINT(Left, Top); var size = new SIZE(Width, Height); var origin = new POINT(0,0);
                var blend = new BLEND { Operation=0, Flags=0, Alpha=255, Format=1 };
                if (UpdateLayeredWindow(Handle, dc, ref position, ref size, memory, ref origin, 0, ref blend, 2)) lastLayer = signature;
            } finally { SelectObject(memory, previous); DeleteObject(dib); DeleteDC(memory); ReleaseDC(IntPtr.Zero, dc); }
        }
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
    static Color AmountColor(double? value, bool isStale) {
        if (!value.HasValue) return Color.FromArgb(153, 156, 164);
        if (value.Value <= 100.0 / 3.0) return Color.FromArgb(255, 116, 119);
        if (value.Value <= 200.0 / 3.0) return Color.FromArgb(255, 209, 102);
        return Color.FromArgb(125, 221, 183);
    }
    static string Percent(double? v) { return v.HasValue ? v.Value.ToString("0.#") + "%" : "--"; }
    static string ResetLabel(DateTime? reset, bool weekly)
    {
        if (!reset.HasValue) return "重置时间 --";
        string[] weekdays = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        return (weekly ? weekdays[(int)reset.Value.DayOfWeek] + " " : "") + reset.Value.ToString("HH:mm") + " 重置";
    }
    void RenderUsage()
    {
        five.Text = "5h " + Percent(fiveLeft) + (stale && fiveLeft.HasValue ? "*" : "");
        week.Text = "每周 " + Percent(weekLeft) + (stale && weekLeft.HasValue ? "*" : "");
        five.ForeColor = AmountColor(fiveLeft, stale); week.ForeColor = AmountColor(weekLeft, stale);
        string detail = "外置大脑剩余额度（每 60 秒自动刷新）\n5 小时：" + Percent(fiveLeft)
            + "\n每周：" + Percent(weekLeft);
        if (fiveReset.HasValue) detail += "\n5 小时重置：" + fiveReset.Value.ToString("MM-dd HH:mm");
        if (weekReset.HasValue) detail += "\n每周重置：" + weekReset.Value.ToString("MM-dd HH:mm");
        if (lastUpdated.HasValue) detail += "\n更新：" + lastUpdated.Value.ToString("HH:mm:ss");
        if (stale) detail += "\n未能更新，带 * 的数字为上次成功读取值。\n" + error;
        detail += "\n双击刷新 · 拖动调整位置 · 右键菜单";
        foreach (var l in new[] { title, five, week }) tip.SetToolTip(l, detail);
        tip.SetToolTip(this, detail);
        DrawLayer();
        WriteStatus();
    }
    void WriteStatus()
    {
        try { File.WriteAllText(Path.Combine(store, "status.json"), json.Serialize(new {
            fiveHourRemaining = fiveLeft, weeklyRemaining = weekLeft, stale = stale,
            fiveHourReset = fiveReset.HasValue ? fiveReset.Value.ToString("o") : null,
            weeklyReset = weekReset.HasValue ? weekReset.Value.ToString("o") : null,
            fiveHourResetLabel = ResetLabel(fiveReset, false), weeklyResetLabel = ResetLabel(weekReset, true),
            updated = lastUpdated.HasValue ? lastUpdated.Value.ToString("o") : null,
            visible = Visible, x = Left, y = Top, width = Width, height = Height, scale = scale,
            hideCount = hideCount, hiddenReason = hiddenReason, taskbarOwned = taskbarOwner != IntPtr.Zero,
            job = Program.JobState,
            error = stale ? error : null
        })); } catch { }
    }
    static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
    static object Field(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v : null; }
    static double? Remaining(Dictionary<string, object> window) {
        var used = Field(window, "usedPercent");
        return used == null ? (double?)null : Math.Max(0, Math.Min(100, 100 - Convert.ToDouble(used)));
    }
    static DateTime? Reset(Dictionary<string, object> window) {
        var unix = Field(window, "resetsAt");
        return unix == null ? (DateTime?)null : new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(Convert.ToDouble(unix)).ToLocalTime();
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
    async Task<string> ReadLine(Process p, DateTime deadline)
    {
        var reading = p.StandardOutput.ReadLineAsync();
        int delay = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
        if (await Task.WhenAny(reading, Task.Delay(delay)) != reading) throw new Exception("读取超时，稍后自动重试。");
        string line = await reading;
        if (line == null) throw new Exception("Codex 用量接口已关闭。");
        return line;
    }
    async Task<Dictionary<string, object>> FetchUsage()
    {
        using (var p = new Process()) {
            p.StartInfo = new ProcessStartInfo(FindCodex(), "app-server") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = store
            };
            p.Start(); p.BeginErrorReadLine();
            try {
                DateTime deadline = DateTime.UtcNow.AddSeconds(25);
                p.StandardInput.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex_taskbar_usage\",\"title\":\"Codex Taskbar Usage\",\"version\":\"1.0\"}}}");
                while (true) {
                    var packet = json.Deserialize<Dictionary<string, object>>(await ReadLine(p, deadline));
                    if (Convert.ToString(Field(packet, "id")) == "1") {
                        if (Field(packet, "error") != null) throw new Exception("Codex 用量接口初始化失败。");
                        break;
                    }
                }
                p.StandardInput.WriteLine("{\"method\":\"initialized\",\"params\":{}}");
                p.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
                while (true) {
                    var packet = json.Deserialize<Dictionary<string, object>>(await ReadLine(p, deadline));
                    if (Convert.ToString(Field(packet, "id")) != "2") continue;
                    if (Field(packet, "error") != null) throw new Exception("无法读取用量，请确认 Codex 已登录。");
                    var result = Obj(Field(packet, "result"));
                    var buckets = Obj(Field(result, "rateLimitsByLimitId"));
                    var limits = Obj(Field(buckets, "codex")) ?? Obj(Field(result, "rateLimits"));
                    if (limits == null) throw new Exception("账号未提供 Codex 额度信息。");
                    return limits;
                }
            } finally { try { if (!p.HasExited) { p.Kill(); p.WaitForExit(2000); } } catch { } }
        }
    }
    async void RefreshUsage()
    {
        if (busy || IsDisposed) return;
        busy = true;
        try {
            var limits = await FetchUsage();
            if (IsDisposed) return;
            var primary = Obj(Field(limits, "primary"));
            var secondary = Obj(Field(limits, "secondary"));
            fiveLeft = Remaining(primary); weekLeft = Remaining(secondary);
            fiveReset = Reset(primary); weekReset = Reset(secondary);
            lastUpdated = DateTime.Now; stale = false; error = null;
        } catch (Exception ex) { if (IsDisposed) return; stale = true; error = ex.Message; }
        finally { busy = false; }
        if (!IsDisposed) RenderUsage();
    }
}
