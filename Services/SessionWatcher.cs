using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// <summary>この経過を超えたセッションは自動でアーカイブする。</summary>
    private static readonly TimeSpan AutoArchiveThreshold = TimeSpan.FromHours(24);

    private readonly string _sessionsDir;
    private readonly string _historyPath;
    private readonly string _archivedPath;
    private readonly string _titlesPath;
    private FileSystemWatcher? _fsWatcher;
    private Timer? _refreshTimer;
    private Timer? _elapsedTimer;

    private readonly object _lock = new();
    private readonly Dictionary<int, SessionInfo> _byPid = new();
    private HashSet<string> _archivedIds = new();
    private Dictionary<string, string> _customTitles = new();

    // ユーザーが手動で Restore したセッション ID。
    // OnElapsedTick で再度自動アーカイブされるのを防ぐ (このアプリ実行中のみ有効)。
    // _lock で保護。
    private readonly HashSet<string> _autoArchiveExempt = new();

    // history.jsonl キャッシュ。sessionId → (lastActivity, firstMessage)。
    // ファイルの LastWriteTime が変わらない限り再スキャンしない。
    // _historyCacheLock で保護。
    private readonly object _historyCacheLock = new();
    private Dictionary<string, (DateTime? lastActivity, string? firstMessage)> _historyCache = new();
    private DateTime _historyCacheStamp = DateTime.MinValue;

    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    /// <summary>セッション状態の更新後(ソート/フィルター再評価タイミング)に発火する。</summary>
    public event Action? DataRefreshed;

    public SessionWatcher()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _sessionsDir  = Path.Combine(home, ".claude", "sessions");
        _historyPath  = Path.Combine(home, ".claude", "history.jsonl");
        _archivedPath = Path.Combine(home, ".claude", "csm_archived.json");
        _titlesPath   = Path.Combine(home, ".claude", "csm_titles.json");
    }

    public void Start()
    {
        _archivedIds  = LoadArchivedIds();
        _customTitles = LoadCustomTitles();
        LoadExistingSessions();
        StartFileWatcher();

        // 5秒ごとにプロセス生死 + ウィンドウ情報 + LastActivity を更新
        _refreshTimer = new Timer(OnRefresh, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // 30秒ごとに経過時間表示を更新 + 24h 経過した項目を自動アーカイブ
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

                // sessionId が空文字のときに history.jsonl の誤エントリとマッチして
                // 無関係なタイムスタンプを拾わないよう、厳密にチェックする。
                (DateTime? lastActivity, string? firstMessage) =
                    !string.IsNullOrWhiteSpace(data.SessionId)
                        ? GetSessionHistoryInfo(data.SessionId!)
                        : (null, null);

                string sid = data.SessionId ?? string.Empty;
                bool explicitlyArchived = _archivedIds.Contains(sid);
                string? customTitle = !string.IsNullOrEmpty(sid) && _customTitles.TryGetValue(sid, out var t)
                    ? t : null;

                // LastActivity(無ければ StartedAt) を基準に 24h 超過で自動アーカイブ。
                var reference = lastActivity ?? startedAt;
                bool autoArchived = (DateTime.Now - reference) >= AutoArchiveThreshold;
                bool isArchived = explicitlyArchived || autoArchived;

                var session = new SessionInfo
                {
                    Pid          = data.Pid,
                    SessionId    = sid,
                    ProjectPath  = data.Cwd ?? string.Empty,
                    StartedAt    = startedAt,
                    IsAlive      = alive,
                    WindowHandle = hwnd,
                    WindowTitle  = title,
                    LastActivity = lastActivity,
                    FirstMessage = firstMessage,
                    CustomTitle  = customTitle,
                    IsArchived   = isArchived,
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
            Thread.Sleep(1000);
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
                (DateTime? la, string? fm) = !string.IsNullOrEmpty(session.SessionId)
                    ? GetSessionHistoryInfo(session.SessionId)
                    : (null, null);

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    session.IsAlive      = alive;
                    session.WindowHandle = hwnd;
                    session.WindowTitle  = title;
                    if (la.HasValue)
                        session.LastActivity = la;
                    if (!string.IsNullOrWhiteSpace(fm) && string.IsNullOrWhiteSpace(session.FirstMessage))
                        session.FirstMessage = fm;
                });
            }

            // UI 更新がすべてキューされた後に一括で自動アーカイブ判定 → 保存 → ビュー再評価を行う。
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (RunAutoArchivePass(snapshot))
                    SaveArchivedIds();
                DataRefreshed?.Invoke();
            });
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

            if (RunAutoArchivePass(snapshot))
                SaveArchivedIds();

            DataRefreshed?.Invoke();
        });
    }

    /// <summary>
    /// ユーザーが手動で Restore したセッションを自動アーカイブ対象外として登録する。
    /// アプリ実行中のみ保持(次回起動時には再び基準に従い判定される)。
    /// </summary>
    public void MarkExemptFromAutoArchive(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_lock) _autoArchiveExempt.Add(sessionId);
    }

    /// <summary>スナップショット内で自動アーカイブ可能なものを一括処理する。変更があれば true。</summary>
    private bool RunAutoArchivePass(IEnumerable<SessionInfo> snapshot)
    {
        bool changed = false;
        foreach (var s in snapshot)
        {
            if (s.IsArchived) continue;

            bool exempt;
            lock (_lock) exempt = !string.IsNullOrEmpty(s.SessionId) && _autoArchiveExempt.Contains(s.SessionId);
            if (exempt) continue;

            if ((DateTime.Now - s.ReferenceTime) < AutoArchiveThreshold) continue;

            s.IsArchived = true;
            if (!string.IsNullOrEmpty(s.SessionId))
                lock (_lock) _archivedIds.Add(s.SessionId);
            changed = true;
        }
        return changed;
    }

    // ---- アーカイブ永続化 ----

    private HashSet<string> LoadArchivedIds()
    {
        if (!File.Exists(_archivedPath)) return new HashSet<string>();
        try
        {
            var json = File.ReadAllText(_archivedPath);
            var ids  = JsonSerializer.Deserialize<List<string>>(json);
            return ids is null ? new HashSet<string>() : new HashSet<string>(ids);
        }
        catch { return new HashSet<string>(); }
    }

    public void SaveArchivedIds()
    {
        List<string> ids;
        lock (_lock)
        {
            // 現在のセッションのアーカイブ状態 + 既知の過去の ID を統合する。
            // 古いセッションファイルが削除されても ID を残すことで復元判断を保持。
            var current = _byPid.Values
                .Where(s => s.IsArchived && !string.IsNullOrEmpty(s.SessionId))
                .Select(s => s.SessionId);
            var notCurrent = _archivedIds
                .Where(id => !_byPid.Values.Any(s => s.SessionId == id));
            ids = current.Concat(notCurrent).Distinct().ToList();
            _archivedIds = new HashSet<string>(ids);
        }
        AtomicWrite(_archivedPath, JsonSerializer.Serialize(ids));
    }

    // ---- カスタムタイトル永続化 ----

    private Dictionary<string, string> LoadCustomTitles()
    {
        if (!File.Exists(_titlesPath)) return new Dictionary<string, string>();
        try
        {
            var json = File.ReadAllText(_titlesPath);
            var map  = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map ?? new Dictionary<string, string>();
        }
        catch { return new Dictionary<string, string>(); }
    }

    /// <summary>
    /// 現在のセッション一覧に基づいてカスタムタイトル map を再構築し、ファイルに永続化する。
    /// </summary>
    public void SaveCustomTitles()
    {
        Dictionary<string, string> map;
        lock (_lock)
        {
            map = _byPid.Values
                .Where(s => !string.IsNullOrEmpty(s.SessionId)
                            && !string.IsNullOrWhiteSpace(s.CustomTitle))
                .GroupBy(s => s.SessionId)
                .ToDictionary(g => g.Key, g => g.First().CustomTitle!);

            // メモリ上の保存済みタイトルのうち、今の一覧にないものも残す。
            foreach (var kv in _customTitles)
                if (!map.ContainsKey(kv.Key))
                    map[kv.Key] = kv.Value;

            _customTitles = new Dictionary<string, string>(map);
        }
        AtomicWrite(_titlesPath, JsonSerializer.Serialize(map));
    }

    /// <summary>tmp 経由の rename でファイル破損を防ぐ。書き込み途中でクラッシュしても旧ファイルは残る。</summary>
    private static void AtomicWrite(string path, string contents)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    // ---- ヘルパー ----

    /// <summary>
    /// history.jsonl を1回スキャンし、全 sessionId についての (firstMessage, lastActivity) を
    /// マップとしてキャッシュする。ファイルの LastWriteTime が変わらない限り再スキャンしない。
    /// これにより 5秒ごとに 50セッション分のフルスキャンが 1スキャンに削減される。
    /// </summary>
    private (DateTime? lastActivity, string? firstMessage) GetSessionHistoryInfo(string sessionId)
    {
        if (!File.Exists(_historyPath)) return (null, null);

        EnsureHistoryCache();

        lock (_historyCacheLock)
        {
            return _historyCache.TryGetValue(sessionId, out var entry)
                ? entry
                : (null, null);
        }
    }

    private void EnsureHistoryCache()
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(_historyPath); }
        catch { return; }

        lock (_historyCacheLock)
        {
            if (_historyCacheStamp == stamp && _historyCache.Count > 0) return;
        }

        // ファイルが更新されている (または初回) 場合のみフルスキャン。
        var fresh = new Dictionary<string, (DateTime? lastActivity, string? firstMessage)>();
        try
        {
            using var fs = new FileStream(_historyPath,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                HistoryEntry? entry = null;
                try { entry = JsonSerializer.Deserialize<HistoryEntry>(line, opts); }
                catch { continue; }

                if (entry == null || string.IsNullOrEmpty(entry.SessionId)) continue;

                fresh.TryGetValue(entry.SessionId, out var cur);

                string? fm = cur.firstMessage ??
                    (!string.IsNullOrWhiteSpace(entry.Display) ? entry.Display : null);
                DateTime? la = entry.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).LocalDateTime
                    : cur.lastActivity;

                fresh[entry.SessionId] = (la, fm);
            }
        }
        catch { return; }

        lock (_historyCacheLock)
        {
            _historyCache = fresh;
            _historyCacheStamp = stamp;
        }
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
