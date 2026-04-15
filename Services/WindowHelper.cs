using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeSessionManager.Services;

/// <summary>
/// Win32 API ラッパー。
/// claude.exe の親チェーンを辿り、ターミナルウィンドウを特定してフォーカスする。
///
/// ケース A: claude → powershell → WindowsTerminal
///   → EnumWindows で WindowsTerminal.exe のウィンドウを探す
///
/// ケース B: claude → powershell → explorer (スタンドアロン PowerShell)
///   → powershell の子の conhost.exe / OpenConsole.exe のウィンドウを探す
///   → コンソールウィンドウはタイトルが空でも拾えるよう ConsoleWindowClass で検索する
/// </summary>
public static class WindowHelper
{
    // ---- P/Invoke ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // SetForegroundWindow の制限を回避するための API
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    // コンソールタイトル取得用
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern uint GetConsoleTitle(StringBuilder lpConsoleTitle, uint nSize);

    private static readonly object ConsoleAttachLock = new();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW    = 5;

    // ---- 定数 ----

    private static readonly HashSet<string> TerminalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal.exe", "powershell.exe", "pwsh.exe",
    };

    private static readonly HashSet<string> StopNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "svchost.exe", "sihost.exe",
        "wininit.exe", "services.exe", "lsass.exe",
    };

    private static readonly HashSet<string> ConsoleHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe", "OpenConsole.exe",
    };

    // コンソールウィンドウのクラス名（Win11 ConPTY）
    private static readonly HashSet<string> ConsoleClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass",            // 従来
        "CASCADIA_HOSTING_WINDOW_CLASS", // Windows Terminal
        "PseudoConsoleWindow",           // Win11 ConPTY（実際に使われるクラス名）
    };

    // ---- public API ----

    /// <summary>ウィンドウをフォアグラウンドに持ってくる。AttachThreadInput で制限を回避する</summary>
    public static void FocusWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;

        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        // SetForegroundWindow はバックグラウンドから呼ぶと Windows にブロックされる。
        // 現在のフォアグラウンドスレッドにアタッチすることで制限を回避する。
        IntPtr fgWnd = GetForegroundWindow();
        uint fgThread  = GetWindowThreadProcessId(fgWnd, out _);
        uint myThread  = GetCurrentThreadId();
        uint tgtThread = GetWindowThreadProcessId(hWnd, out _);

        bool attached = false;
        if (fgThread != myThread)
        {
            attached = AttachThreadInput(myThread, fgThread, true);
        }

        try
        {
            BringWindowToTop(hWnd);
            ShowWindow(hWnd, SW_SHOW);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(myThread, fgThread, false);
        }
    }

    /// <summary>
    /// claude.exe の PID からターミナルウィンドウのハンドルを返す。
    /// WMI クエリを含むため、必ずバックグラウンドスレッドから呼ぶこと。
    /// </summary>
    public static IntPtr FindWindowForProcess(int claudePid)
    {
        var (termPid, termName) = FindTerminalAncestor(claudePid);
        if (termPid <= 0) return IntPtr.Zero;

        if (termName.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase))
        {
            // ケース A: Windows Terminal のトップレベルウィンドウ（タイトルあり）
            return FindWindowByPidWithTitle(termPid);
        }
        else
        {
            // ケース B: スタンドアロン PowerShell
            // Win11 ConPTY では PseudoConsoleWindow が powershell.exe 自身のPIDに属する
            IntPtr hwnd = FindConsoleWindowByPid(termPid);
            if (hwnd != IntPtr.Zero) return hwnd;

            // フォールバック: タイトルありウィンドウを探す
            return FindWindowByPidWithTitle(termPid);
        }
    }

    public static string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return string.Empty;
        var sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// HWND のタイトルを取得する。
    /// PseudoConsoleWindow はタイトルが空のため、AttachConsole で取得したコンソールタイトルにフォールバックする。
    /// WMI クエリを含むため、バックグラウンドスレッドから呼ぶこと。
    /// </summary>
    public static string GetEffectiveTitle(IntPtr hWnd, int claudePid)
    {
        var title = GetWindowTitle(hWnd);
        if (!string.IsNullOrWhiteSpace(title)) return title;

        // PseudoConsoleWindow などタイトルが空の場合、コンソールタイトルを使う
        var (termPid, _) = FindTerminalAncestor(claudePid);
        if (termPid <= 0) return string.Empty;
        return GetConsoleTitleForPid(termPid);
    }

    /// <summary>AttachConsole でコンソールタイトルを取得する（同時実行不可のためロック付き）</summary>
    private static string GetConsoleTitleForPid(int pid)
    {
        lock (ConsoleAttachLock)
        {
            if (!AttachConsole((uint)pid)) return string.Empty;
            try
            {
                var sb = new StringBuilder(512);
                GetConsoleTitle(sb, (uint)sb.Capacity);
                return sb.ToString();
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    // ---- private ----

    /// <summary>claude.exe → 祖先を辿り、最初のターミナルプロセスを返す</summary>
    private static (int pid, string name) FindTerminalAncestor(int claudePid)
    {
        int cur = claudePid;
        for (int depth = 0; depth < 8; depth++)
        {
            int parentPid = GetParentPid(cur);
            if (parentPid <= 1) break;

            string parentName = GetProcessName(parentPid);
            if (string.IsNullOrEmpty(parentName)) break;

            if (TerminalNames.Contains(parentName))
                return (parentPid, parentName);

            if (StopNames.Contains(parentName))
                break;

            cur = parentPid;
        }
        return (-1, string.Empty);
    }

    /// <summary>指定 PID の子プロセスから conhost.exe / OpenConsole.exe を探す</summary>
    private static int FindConsoleHostChild(int shellPid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, Name FROM Win32_Process WHERE ParentProcessId = {shellPid}");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? string.Empty;
                if (ConsoleHostNames.Contains(name))
                    return Convert.ToInt32(obj["ProcessId"]);
            }
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// EnumWindows で ConsoleWindowClass のウィンドウを指定 PID で探す。
    /// コンソールウィンドウはタイトルが空の場合もあるのでタイトルチェックなし。
    /// </summary>
    private static IntPtr FindConsoleWindowByPid(int targetPid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint winPid);
            if ((int)winPid != targetPid) return true;

            var cls = new StringBuilder(128);
            GetClassName(hWnd, cls, cls.Capacity);
            if (ConsoleClassNames.Contains(cls.ToString()))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>EnumWindows で指定 PID に紐付くトップレベルウィンドウ（タイトルあり）を返す</summary>
    private static IntPtr FindWindowByPidWithTitle(int targetPid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint winPid);
            if ((int)winPid != targetPid) return true;

            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (string.IsNullOrWhiteSpace(sb.ToString())) return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static int GetParentPid(int pid)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject o in s.Get())
                return Convert.ToInt32(o["ParentProcessId"]);
        }
        catch { }
        return -1;
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject o in s.Get())
                return o["Name"]?.ToString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }
}
