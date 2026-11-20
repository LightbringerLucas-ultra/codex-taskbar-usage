using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

[Flags]
internal enum Clients { None = 0, Codex = 1, ClaudeCode = 2 }

internal static class ClientProcesses
{
    // Take one process-only snapshot. Process.GetProcessesByName on .NET Framework
    // builds managed process/thread information for the whole machine on every call.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint size, usage;
        public int pid;
        public UIntPtr heap;
        public uint moduleId, threads, parentPid;
        public int basePriority;
        public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string name;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

    delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll", SetLastError = true)] static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("kernel32.dll")] static extern int GetCurrentProcessId();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(int pid, out uint session);
    [DllImport("kernel32.dll")] static extern SafeFileHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder path, ref int length);

    static HashSet<int> MainWindowProcesses()
    {
        const uint GetOwner = 4;
        var result = new HashSet<int>();
        // A minimized main window is visible to this API. Owned and hidden utility windows are excluded.
        bool ok = EnumWindows(delegate(IntPtr window, IntPtr state) {
            int pid;
            if (IsWindowVisible(window) && GetWindow(window, GetOwner) == IntPtr.Zero
                && GetWindowThreadProcessId(window, out pid) != 0) result.Add(pid);
            return true;
        }, IntPtr.Zero);
        if (!ok) throw new Win32Exception();
        return result;
    }

    static string ExecutablePath(int pid)
    {
        const uint QueryLimitedInformation = 0x1000;
        using (var process = OpenProcess(QueryLimitedInformation, false, pid)) {
            if (process.IsInvalid) return null;
            int length = 512;
            var path = new StringBuilder(length);
            if (QueryFullProcessImageName(process, 0, path, ref length)) return path.ToString();
            if (Marshal.GetLastWin32Error() != 122) return null;
            length = 32768;
            path.EnsureCapacity(length);
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
        }
    }

    internal static Clients Read()
    {
        const uint ProcessSnapshot = 2;
        const int NoMoreFiles = 18;
        Clients found = Clients.None;
        HashSet<int> windowProcesses = null;
        uint desktopSession;
        if (!ProcessIdToSessionId(GetCurrentProcessId(), out desktopSession)) throw new Win32Exception();
        using (var snapshot = CreateToolhelp32Snapshot(ProcessSnapshot, 0)) {
            if (snapshot.IsInvalid) throw new Win32Exception();
            var entry = new ProcessEntry { size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
            bool available = Process32FirstW(snapshot, ref entry);
            while (available) {
                bool codex = String.Equals(entry.name, "Codex.exe", StringComparison.OrdinalIgnoreCase);
                bool chat = String.Equals(entry.name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
                bool claude = String.Equals(entry.name, "claude.exe", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(entry.name, "claude-code.exe", StringComparison.OrdinalIgnoreCase);
                if ((codex || chat) && (found & Clients.Codex) == 0) {
                    if (windowProcesses == null) windowProcesses = MainWindowProcesses();
                    if (windowProcesses.Contains(entry.pid) && (codex || (ExecutablePath(entry.pid) ?? "").IndexOf(
                        @"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0)) found |= Clients.Codex;
                } else if (claude && (found & Clients.ClaudeCode) == 0) {
                    uint session;
                    if (ProcessIdToSessionId(entry.pid, out session) && session == desktopSession
                        && IsClaudeCodeExecutable(ExecutablePath(entry.pid))) found |= Clients.ClaudeCode;
                }
                if (found == (Clients.Codex | Clients.ClaudeCode)) return found;
                available = Process32NextW(snapshot, ref entry);
            }
            int error = Marshal.GetLastWin32Error();
            if (error != NoMoreFiles) throw new Win32Exception(error);
        }
        return found;
    }

    internal static bool IsClaudeCodeExecutable(string path)
    {
        if (String.IsNullOrEmpty(path)) return false; // May have exited since the snapshot.
        string normalized = path.Replace('/', '\\').ToLowerInvariant();
        if (normalized.EndsWith(@"\claude-code.exe", StringComparison.Ordinal)) return true;
        if (!normalized.EndsWith(@"\claude.exe", StringComparison.Ordinal)) return false;
        return normalized.Contains(@"\@anthropic-ai\claude-code\")
            || normalized.Contains(@"\@anthropic-ai\claude-code-win32-")
            || normalized.Contains(@"\.local\bin\")
            || normalized.Contains(@"\.local\share\claude\")
            || normalized.Contains(@"\winget\packages\anthropic.claudecode_")
            || normalized.Contains(@"\scoop\apps\claude-code\")
            || normalized.Contains(@"\.bun\bin\");
    }
}
