using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

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
    /// WindowsTerminal は 1プロセスで複数タブ/ウィンドウを持ち、
    /// GetWindowText が「アクティブタブ」のタイトルしか返さないため、
    /// 同じ WT で動く別セッションのタイトルが混線する。
    /// そのため WindowsTerminal を親に持つ場合は空を返し、
    /// UI 側はプロジェクト名を主タイトルとして表示する。
    /// AttachConsole は他プロセスのコンソールを乗っ取る危険があるため使用しない。
    /// </summary>
    public static string GetEffectiveTitle(IntPtr hWnd, int claudePid)
    {
        var (_, termName) = FindTerminalAncestor(claudePid);

        // WindowsTerminal の場合はアクティブタブのタイトルが他セッションに流入するため使わない
        if (termName.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        // スタンドアロン PowerShell / conhost のタイトルは個別ウィンドウなので信頼できる
        var title = GetWindowTitle(hWnd);
        return IsMeaningfulTitle(title) ? title : string.Empty;
    }

    /// <summary>
    /// 「Windows PowerShell」「PowerShell」「cmd.exe」などの汎用シェル名タイトルは
    /// セッション識別に役立たないのでノイズ扱いにする。
    /// </summary>
    private static bool IsMeaningfulTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var trimmed = title.Trim();
        if (GenericShellTitles.Contains(trimmed)) return false;
        return true;
    }

    private static readonly HashSet<string> GenericShellTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows PowerShell",
        "PowerShell",
        "pwsh",
        "cmd.exe",
        "Command Prompt",
        "コマンド プロンプト",
    };

    // ---- private ----

    /// <summary>
    /// claude.exe → 祖先を辿り、最適なターミナルプロセスを返す。
    /// WindowsTerminal を最優先とし、powershell/pwsh の上に WT がある場合は WT を返す。
    /// WT がなければ powershell/pwsh を返す（スタンドアロン）。
    /// </summary>
    private static (int pid, string name) FindTerminalAncestor(int claudePid)
    {
        int cur = claudePid;
        int savedShellPid  = -1;
        string savedShellName = string.Empty;

        for (int depth = 0; depth < 8; depth++)
        {
            int parentPid = GetParentPid(cur);
            if (parentPid <= 1) break;

            string parentName = GetProcessName(parentPid);
            if (string.IsNullOrEmpty(parentName)) break;

            // WindowsTerminal を見つけたら最優先で返す
            if (parentName.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase))
                return (parentPid, parentName);

            // powershell / pwsh: 記録して上方向に WT を探し続ける
            if (parentName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
                parentName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (savedShellPid < 0) { savedShellPid = parentPid; savedShellName = parentName; }
                cur = parentPid;
                continue;
            }

            if (StopNames.Contains(parentName)) break;

            cur = parentPid;
        }

        // WT が見つからなかった → スタンドアロン shell を返す
        return (savedShellPid, savedShellName);
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
