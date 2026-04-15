using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClaudeSessionManager.Models;

namespace ClaudeSessionManager.Services;

/// <summary>
/// ~/.claude/sessions/*.json を監視し、Claude Codeセッション一覧を維持する。
/// </summary>
public sealed class SessionWatcher : IDisposable
{
    private readonly string _sessionsDir;
    private readonly string _historyPath;
    private FileSystemWatcher? _fsWatcher;
    private Timer? _refreshTimer;
    private Timer? _elapsedTimer;

    private readonly object _lock = new();
    private readonly Dictionary<int, SessionInfo> _byPid = new();

    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    public SessionWatcher()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _sessionsDir  = Path.Combine(home, ".claude", "sessions");
        _historyPath  = Path.Combine(home, ".claude", "history.jsonl");
    }

    public void Start()
    {
        LoadExistingSessions();
        StartFileWatcher();

        // 5秒ごとにプロセス生死 + ウィンドウ情報 + LastActivity を更新
        _refreshTimer = new Timer(OnRefresh, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // 30秒ごとに経過時間表示を更新
        _elapsedTimer = new Timer(OnElapsedTick, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    // ---- ファイル読み込み ----

    private void LoadExistingSessions()
    {
        if (!Directory.Exists(_sessionsDir)) return;
        foreach (var file in Directory.GetFiles(_sessionsDir, "*.json"))
            TryAddFromFile(file);
    }

    private void TryAddFromFile(string filePath)
    {
        Task.Run(() =>
        {
            try
            {
                var text = File.ReadAllText(filePath);
                var data = JsonSerializer.Deserialize<SessionFileData>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data is null || data.Pid <= 0) return;

                var startedAt = data.StartedAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(data.StartedAt).LocalDateTime
                    : DateTime.Now;

                bool alive = IsAlive(data.Pid);
                IntPtr hwnd  = alive ? WindowHelper.FindWindowForProcess(data.Pid) : IntPtr.Zero;
                string title = alive
                    ? WindowHelper.GetEffectiveTitle(hwnd, data.Pid)
                    : string.Empty;
                var lastActivity = data.SessionId != null
                    ? GetLastActivity(data.SessionId)
                    : null;

                var session = new SessionInfo
                {
                    Pid          = data.Pid,
                    SessionId    = data.SessionId ?? string.Empty,
                    ProjectPath  = data.Cwd ?? string.Empty,
                    StartedAt    = startedAt,
                    IsAlive      = alive,
                    WindowHandle = hwnd,
                    WindowTitle  = title,
                    LastActivity = lastActivity,
                };

                lock (_lock)
                {
                    if (_byPid.ContainsKey(data.Pid)) return;
                    _byPid[data.Pid] = session;
                }

                Application.Current.Dispatcher.BeginInvoke(() => Sessions.Add(session));
            }
            catch { }
        });
    }

    // ---- FileSystemWatcher ----

    private void StartFileWatcher()
    {
        if (!Directory.Exists(_sessionsDir)) return;

        _fsWatcher = new FileSystemWatcher(_sessionsDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _fsWatcher.Created += (_, e) =>
        {
            Thread.Sleep(300);
            TryAddFromFile(e.FullPath);
        };
        _fsWatcher.Deleted += (_, e) =>
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(e.Name), out int pid))
                MarkDead(pid);
        };
    }

    // ---- 定期リフレッシュ ----

    private void OnRefresh(object? _)
    {
        List<SessionInfo> snapshot;
        lock (_lock)
            snapshot = new List<SessionInfo>(_byPid.Values);

        Task.Run(() =>
        {
            foreach (var session in snapshot)
            {
                bool alive       = IsAlive(session.Pid);
                IntPtr hwnd      = alive ? WindowHelper.FindWindowForProcess(session.Pid) : IntPtr.Zero;
                string title     = alive ? WindowHelper.GetEffectiveTitle(hwnd, session.Pid) : string.Empty;
                var lastActivity = !string.IsNullOrEmpty(session.SessionId)
                    ? GetLastActivity(session.SessionId)
                    : null;

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    session.IsAlive      = alive;
                    session.WindowHandle = hwnd;
                    session.WindowTitle  = title;
                    if (lastActivity.HasValue)
                        session.LastActivity = lastActivity;
                });
            }
        });
    }

    private void OnElapsedTick(object? _)
    {
        List<SessionInfo> snapshot;
        lock (_lock)
            snapshot = new List<SessionInfo>(_byPid.Values);

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var s in snapshot)
                s.NotifyTimesChanged();
        });
    }

    // ---- ヘルパー ----

    /// <summary>
    /// ~/.claude/history.jsonl の末尾を読んで sessionId が一致する最後のエントリの日時を返す。
    /// </summary>
    private DateTime? GetLastActivity(string sessionId)
    {
        if (!File.Exists(_historyPath)) return null;
        try
        {
            // 末尾から最大 2000 行を保持しながら読む
            var tail = new Queue<string>(2000);
            using var fs = new FileStream(_historyPath,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                tail.Enqueue(line);
                if (tail.Count > 2000) tail.Dequeue();
            }

            // 末尾から sessionId を探す
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var l in System.Linq.Enumerable.Reverse(tail))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(l, opts);
                    if (entry?.SessionId == sessionId && entry.Timestamp > 0)
                        return DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).LocalDateTime;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private void MarkDead(int pid)
    {
        lock (_lock)
        {
            if (!_byPid.TryGetValue(pid, out var session)) return;
            Application.Current.Dispatcher.BeginInvoke(() => session.IsAlive = false);
        }
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    // ---- IDisposable ----

    public void Dispose()
    {
        _fsWatcher?.Dispose();
        _refreshTimer?.Dispose();
        _elapsedTimer?.Dispose();
    }

    // ---- JSON レコード ----

    private sealed record SessionFileData(
        int Pid,
        string? SessionId,
        string? Cwd,
        long StartedAt
    );

    private sealed record HistoryEntry(
        string? SessionId,
        long Timestamp,
        string? Display
    );
}
