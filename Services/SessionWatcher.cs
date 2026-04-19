using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClaudeSessionManager.Models;

namespace ClaudeSessionManager.Services;

public sealed class SessionWatcher : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ElapsedInterval = TimeSpan.FromSeconds(30);

    private readonly string _claudeSessionsDir;
    private readonly string _claudeHistoryPath;
    private readonly string _codexSessionsDir;
    private readonly string _codexHistoryPath;
    private readonly string _geminiTmpDir;
    private readonly string _settingsDir;
    private readonly string _cachePath;
    private readonly string _titlesPath;
    private readonly string _starsPath;

    private readonly object _lock = new();
    private readonly Dictionary<string, SessionInfo> _byKey = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> _customTitles = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _starredKeys = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _refreshTimer;
    private Timer? _elapsedTimer;

    private readonly object _claudeHistoryLock = new();
    private Dictionary<string, ClaudeHistoryInfo> _claudeHistoryCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _claudeHistoryStamp = DateTime.MinValue;

    private readonly object _codexHistoryLock = new();
    private Dictionary<string, CodexHistoryInfo> _codexHistoryCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _codexHistoryStamp = DateTime.MinValue;

    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    public event Action? DataRefreshed;

    public SessionWatcher()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _claudeSessionsDir = Path.Combine(home, ".claude", "sessions");
        _claudeHistoryPath = Path.Combine(home, ".claude", "history.jsonl");
        _codexSessionsDir = Path.Combine(home, ".codex", "sessions");
        _codexHistoryPath = Path.Combine(home, ".codex", "history.jsonl");
        _geminiTmpDir = Path.Combine(home, ".gemini", "tmp");

        _settingsDir = Path.Combine(home, ".claude");
        _cachePath = Path.Combine(_settingsDir, "csm_sessions.json");
        _titlesPath = Path.Combine(_settingsDir, "csm_titles.json");
        _starsPath = Path.Combine(_settingsDir, "csm_stars.json");
    }

    public void Start()
    {
        _customTitles = LoadCustomTitles();
        _starredKeys = LoadStarredKeys();
        LoadCachedSessions();
        RefreshSessions();

        _refreshTimer = new Timer(OnRefresh, null, RefreshInterval, RefreshInterval);
        _elapsedTimer = new Timer(OnElapsedTick, null, ElapsedInterval, ElapsedInterval);
    }

    public void SaveState()
    {
        lock (_lock)
        {
            _customTitles = _byKey.Values
                .Where(s => !string.IsNullOrWhiteSpace(s.CustomTitle))
                .ToDictionary(s => s.StorageKey, s => s.CustomTitle!, StringComparer.OrdinalIgnoreCase);

            _starredKeys = _byKey.Values
                .Where(s => s.IsStarred)
                .Select(s => s.StorageKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        AtomicWriteJson(_titlesPath, _customTitles);
        AtomicWriteJson(_starsPath, _starredKeys.ToList());
        SaveSessionCache();
    }

    public void SetArchived(SessionInfo session, bool archived)
    {
        lock (_lock)
        {
            session.IsManuallyArchived = archived;
        }

        SaveState();
        DataRefreshed?.Invoke();
    }

    public void SetDeleted(SessionInfo session, bool deleted)
    {
        lock (_lock)
        {
            session.IsDeleted = deleted;
        }

        SaveState();
        DataRefreshed?.Invoke();
    }

    public void RefreshSessions()
    {
        Task.Run(() =>
        {
            try
            {
                var snapshots = DiscoverAllSessions();
                Application.Current.Dispatcher.BeginInvoke(() => ApplySnapshots(snapshots));
            }
            catch (Exception ex)
            {
                LogDebug($"RefreshSessions failed: {ex}");
            }
        });
    }

    private void OnRefresh(object? _)
    {
        RefreshSessions();
    }

    private void OnElapsedTick(object? _)
    {
        List<SessionInfo> snapshot;

        lock (_lock)
            snapshot = _byKey.Values.ToList();

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var session in snapshot)
                session.NotifyTimesChanged();

            DataRefreshed?.Invoke();
        });
    }

    private List<DiscoveredSession> DiscoverAllSessions()
    {
        var sessions = new List<DiscoveredSession>();
        sessions.AddRange(DiscoverClaudeSessions());
        sessions.AddRange(DiscoverCodexSessions());
        sessions.AddRange(DiscoverGeminiSessions());
        return sessions;
    }

    private IEnumerable<DiscoveredSession> DiscoverClaudeSessions()
    {
        if (!Directory.Exists(_claudeSessionsDir))
            yield break;

        foreach (var path in Directory.GetFiles(_claudeSessionsDir, "*.json"))
        {
            if (!TryReadAllTextWithRetry(path, out var text))
                continue;

            ClaudeSessionFileData? data;
            try
            {
                data = JsonSerializer.Deserialize<ClaudeSessionFileData>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to parse Claude session file '{path}': {ex.Message}");
                continue;
            }

            if (data is null || data.Pid <= 0 || string.IsNullOrWhiteSpace(data.SessionId))
                continue;

            var startedAt = data.StartedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(data.StartedAt).LocalDateTime
                : File.GetLastWriteTime(path);

            var history = GetClaudeHistoryInfo(data.SessionId);
            var alive = IsAlive(data.Pid);
            var windowHandle = alive ? WindowHelper.FindWindowForProcess(data.Pid) : IntPtr.Zero;
            var windowTitle = alive ? WindowHelper.GetEffectiveTitle(windowHandle, data.Pid) : string.Empty;

            yield return new DiscoveredSession(
                StorageKey: BuildStorageKey(SessionSourceApp.Claude, data.SessionId),
                SourceApp: SessionSourceApp.Claude,
                SessionId: data.SessionId,
                ProjectPath: data.Cwd ?? string.Empty,
                StartedAt: startedAt,
                LastActivity: history.LastActivity,
                FirstMessage: history.FirstMessage,
                IsAlive: alive,
                Pid: data.Pid,
                WindowHandle: windowHandle,
                WindowTitle: windowTitle);
        }
    }

    private IEnumerable<DiscoveredSession> DiscoverCodexSessions()
    {
        var historyById = GetCodexHistorySessions();
        if (historyById.Count == 0)
            yield break;

        var metaById = GetCodexSessionMetaMap();
        var liveById = MatchRunningCodexProcesses(metaById);

        foreach (var pair in historyById.OrderByDescending(x => x.Value.LastActivity ?? x.Value.StartedAt))
        {
            var sessionId = pair.Key;
            var history = pair.Value;
            metaById.TryGetValue(sessionId, out var meta);
            if (meta is { IsInteractive: false })
                continue;

            liveById.TryGetValue(sessionId, out var live);

            var startedAt = meta?.StartedAt ?? history.StartedAt;
            var projectPath = meta?.ProjectPath ?? string.Empty;
            var pid = live?.Pid ?? 0;
            var isAlive = pid > 0;
            var windowHandle = isAlive ? live!.WindowHandle : IntPtr.Zero;
            var windowTitle = isAlive ? live!.WindowTitle : string.Empty;

            yield return new DiscoveredSession(
                StorageKey: BuildStorageKey(SessionSourceApp.Codex, sessionId),
                SourceApp: SessionSourceApp.Codex,
                SessionId: sessionId,
                ProjectPath: projectPath,
                StartedAt: startedAt,
                LastActivity: history.LastActivity,
                FirstMessage: history.FirstMessage,
                IsAlive: isAlive,
                Pid: pid,
                WindowHandle: windowHandle,
                WindowTitle: windowTitle);
        }
    }

    private IEnumerable<DiscoveredSession> DiscoverGeminiSessions()
    {
        if (!Directory.Exists(_geminiTmpDir))
            yield break;

        var latestByProject = new Dictionary<string, GeminiDiscoveredSession>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.GetFiles(_geminiTmpDir, "session-*.json", SearchOption.AllDirectories))
        {
            if (!TryReadAllTextWithRetry(path, out var text))
                continue;

            GeminiSessionFileData? data;
            try
            {
                data = JsonSerializer.Deserialize<GeminiSessionFileData>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to parse Gemini session file '{path}': {ex.Message}");
                continue;
            }

            if (data is null || string.IsNullOrWhiteSpace(data.SessionId))
                continue;

            var projectPath = ResolveGeminiProjectPath(path);
            var startedAt = ParseLocalDateTime(data.StartTime) ?? File.GetLastWriteTime(path);
            var lastActivity = ParseLocalDateTime(data.LastUpdated) ?? startedAt;
            var firstMessage = data.Messages?
                .FirstOrDefault(m => string.Equals(m.Type, "user", StringComparison.OrdinalIgnoreCase))
                ?.Content?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Text))?.Text;

            var projectKey = BuildGeminiProjectKey(projectPath, path);
            var candidate = new GeminiDiscoveredSession(
                StorageKey: projectKey,
                SessionId: data.SessionId,
                ProjectPath: projectPath,
                StartedAt: startedAt,
                LastActivity: lastActivity,
                FirstMessage: firstMessage);

            if (!latestByProject.TryGetValue(projectKey, out var current) ||
                candidate.LastActivity > current.LastActivity ||
                (candidate.LastActivity == current.LastActivity && candidate.StartedAt > current.StartedAt))
            {
                latestByProject[projectKey] = candidate;
            }
        }

        foreach (var item in latestByProject.Values.OrderByDescending(x => x.LastActivity))
        {
            yield return new DiscoveredSession(
                StorageKey: item.StorageKey,
                SourceApp: SessionSourceApp.Gemini,
                SessionId: item.SessionId,
                ProjectPath: item.ProjectPath,
                StartedAt: item.StartedAt,
                LastActivity: item.LastActivity,
                FirstMessage: item.FirstMessage,
                IsAlive: false,
                Pid: 0,
                WindowHandle: IntPtr.Zero,
                WindowTitle: string.Empty);
        }
    }

    private void ApplySnapshots(IEnumerable<DiscoveredSession> discovered)
    {
        var discoveredByKey = discovered
            .GroupBy(s => s.StorageKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LastActivity ?? x.StartedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            foreach (var item in discoveredByKey.Values)
            {
                if (_byKey.TryGetValue(item.StorageKey, out var existing))
                {
                    UpdateSession(existing, item);
                }
                else
                {
                    var created = CreateSession(item);
                    _byKey[item.StorageKey] = created;
                    Sessions.Add(created);
                }
            }

            foreach (var session in _byKey.Values)
            {
                if (discoveredByKey.ContainsKey(session.StorageKey))
                    continue;

                if (session.SourceApp != SessionSourceApp.Claude)
                    continue;

                session.IsAlive = false;
                session.Pid = 0;
                session.WindowHandle = IntPtr.Zero;
                session.WindowTitle = string.Empty;
            }

            var staleKeys = _byKey.Values
                .Where(session => session.SourceApp != SessionSourceApp.Claude &&
                                  !discoveredByKey.ContainsKey(session.StorageKey))
                .Select(session => session.StorageKey)
                .ToList();

            foreach (var staleKey in staleKeys)
            {
                if (!_byKey.Remove(staleKey, out var removed))
                    continue;

                Sessions.Remove(removed);
            }
        }

        SaveSessionCache();
        DataRefreshed?.Invoke();
    }

    private SessionInfo CreateSession(DiscoveredSession item)
    {
        var session = new SessionInfo
        {
            StorageKey = item.StorageKey,
            SourceApp = item.SourceApp,
            SessionId = item.SessionId,
            ProjectPath = item.ProjectPath,
            StartedAt = item.StartedAt,
            LastActivity = item.LastActivity,
            FirstMessage = item.FirstMessage,
            IsAlive = item.IsAlive,
            Pid = item.Pid,
            WindowHandle = item.WindowHandle,
            WindowTitle = item.WindowTitle,
            CustomTitle = _customTitles.TryGetValue(item.StorageKey, out var title) ? title : null,
            IsStarred = _starredKeys.Contains(item.StorageKey),
            IsManuallyArchived = false,
            IsDeleted = false,
        };

        return session;
    }

    private static void UpdateSession(SessionInfo existing, DiscoveredSession item)
    {
        existing.SourceApp = item.SourceApp;
        existing.ProjectPath = item.ProjectPath;
        existing.LastActivity = item.LastActivity;
        if (!string.IsNullOrWhiteSpace(item.FirstMessage))
            existing.FirstMessage = item.FirstMessage;
        existing.IsAlive = item.IsAlive;
        existing.Pid = item.Pid;
        existing.WindowHandle = item.WindowHandle;
        existing.WindowTitle = item.WindowTitle;
    }

    private void LoadCachedSessions()
    {
        var records = ReadJsonOrDefault<List<PersistedSessionRecord>>(_cachePath) ?? new List<PersistedSessionRecord>();

        foreach (var record in records)
        {
            var key = !string.IsNullOrWhiteSpace(record.StorageKey)
                ? record.StorageKey
                : BuildStorageKey(record.SourceApp, record.SessionId);
            var session = new SessionInfo
            {
                StorageKey = key,
                SourceApp = record.SourceApp,
                SessionId = record.SessionId,
                ProjectPath = record.ProjectPath ?? string.Empty,
                StartedAt = record.StartedAt,
                LastActivity = record.LastActivity,
                FirstMessage = record.FirstMessage,
                IsAlive = false,
                Pid = 0,
                WindowHandle = IntPtr.Zero,
                WindowTitle = string.Empty,
                CustomTitle = _customTitles.TryGetValue(key, out var title) ? title : null,
                IsStarred = _starredKeys.Contains(key),
                IsManuallyArchived = record.IsManuallyArchived,
                IsDeleted = record.IsDeleted,
            };

            _byKey[key] = session;
            Sessions.Add(session);
        }
    }

    private void SaveSessionCache()
    {
        List<PersistedSessionRecord> records;

        lock (_lock)
        {
            records = _byKey.Values
                .Select(s => new PersistedSessionRecord(
                    s.StorageKey,
                    s.SourceApp,
                    s.SessionId,
                    s.ProjectPath,
                    s.StartedAt,
                    s.LastActivity,
                    s.FirstMessage,
                    s.IsManuallyArchived,
                    s.IsDeleted))
                .ToList();
        }

        AtomicWriteJson(_cachePath, records);
    }

    private Dictionary<string, string> LoadCustomTitles()
    {
        return ReadJsonOrDefault<Dictionary<string, string>>(_titlesPath)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> LoadStarredKeys()
    {
        var keys = ReadJsonOrDefault<List<string>>(_starsPath) ?? new List<string>();
        return new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
    }

    private ClaudeHistoryInfo GetClaudeHistoryInfo(string sessionId)
    {
        EnsureClaudeHistoryCache();
        lock (_claudeHistoryLock)
        {
            return _claudeHistoryCache.TryGetValue(sessionId, out var info)
                ? info
                : new ClaudeHistoryInfo(null, null);
        }
    }

    private CodexHistoryInfo GetCodexHistoryInfo(string sessionId)
    {
        EnsureCodexHistoryCache();
        lock (_codexHistoryLock)
        {
            return _codexHistoryCache.TryGetValue(sessionId, out var info)
                ? info
                : new CodexHistoryInfo(DateTime.MinValue, null, null);
        }
    }

    private Dictionary<string, CodexHistoryInfo> GetCodexHistorySessions()
    {
        EnsureCodexHistoryCache();
        lock (_codexHistoryLock)
        {
            return new Dictionary<string, CodexHistoryInfo>(_codexHistoryCache, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void EnsureClaudeHistoryCache()
    {
        EnsureHistoryCache(
            _claudeHistoryPath,
            _claudeHistoryLock,
            ref _claudeHistoryStamp,
            ref _claudeHistoryCache,
            ParseClaudeHistoryLine);
    }

    private void EnsureCodexHistoryCache()
    {
        var fresh = EnsureCodexHistoryCacheInternal();
        if (fresh == null)
            return;

        lock (_codexHistoryLock)
        {
            _codexHistoryCache = fresh.Value.Cache;
            _codexHistoryStamp = fresh.Value.Stamp;
        }
    }

    private (Dictionary<string, CodexHistoryInfo> Cache, DateTime Stamp)? EnsureCodexHistoryCacheInternal()
    {
        if (!File.Exists(_codexHistoryPath))
            return null;

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(_codexHistoryPath);
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to stat Codex history: {ex.Message}");
            return null;
        }

        lock (_codexHistoryLock)
        {
            if (_codexHistoryStamp == stamp && _codexHistoryCache.Count > 0)
                return null;
        }

        if (!TryReadAllTextWithRetry(_codexHistoryPath, out var text))
            return null;

        var cache = new Dictionary<string, CodexHistoryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in EnumerateNonEmptyLines(text))
        {
            try
            {
                var entry = ParseCodexHistoryLine(line);
                if (entry is null || string.IsNullOrWhiteSpace(entry.SessionId))
                    continue;

                cache.TryGetValue(entry.SessionId, out var current);
                current ??= new CodexHistoryInfo(entry.Timestamp ?? DateTime.MinValue, null, null);

                var first = current.FirstMessage ?? entry.Text;
                var startedAt = current.StartedAt == DateTime.MinValue
                    ? entry.Timestamp ?? DateTime.MinValue
                    : current.StartedAt;
                DateTime? lastActivity = entry.Timestamp;

                cache[entry.SessionId] = new CodexHistoryInfo(startedAt, lastActivity, first);
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to parse Codex history line: {ex.Message}");
            }
        }

        return (cache, stamp);
    }

    private static void EnsureHistoryCache<T>(
        string path,
        object sync,
        ref DateTime stampField,
        ref Dictionary<string, T> cacheField,
        Func<string, (string SessionId, T Value)?> lineParser)
    {
        if (!File.Exists(path))
            return;

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to stat history '{path}': {ex.Message}");
            return;
        }

        lock (sync)
        {
            if (stampField == stamp && cacheField.Count > 0)
                return;
        }

        if (!TryReadAllTextWithRetry(path, out var text))
            return;

        var fresh = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in EnumerateNonEmptyLines(text))
        {
            try
            {
                var parsed = lineParser(line);
                if (parsed is null)
                    continue;

                fresh[parsed.Value.SessionId] = parsed.Value.Value;
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to parse history line in '{path}': {ex.Message}");
            }
        }

        lock (sync)
        {
            cacheField = fresh;
            stampField = stamp;
        }
    }

    private static (string SessionId, ClaudeHistoryInfo Value)? ParseClaudeHistoryLine(string line)
    {
        var entry = JsonSerializer.Deserialize<ClaudeHistoryEntry>(line,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (entry is null || string.IsNullOrWhiteSpace(entry.SessionId))
            return null;

        var info = new ClaudeHistoryInfo(
            entry.Timestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).LocalDateTime : null,
            string.IsNullOrWhiteSpace(entry.Display) ? null : entry.Display);

        return (entry.SessionId, info);
    }

    private static CodexSessionMeta? ParseCodexSessionMeta(string text)
    {
        foreach (var line in EnumerateNonEmptyLines(text))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<CodexSessionLine>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (entry is null || !string.Equals(entry.Type, "session_meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                var payload = entry.Payload;
                if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
                    return null;

                var startedAt = ParseLocalDateTime(payload.Timestamp) ?? DateTime.Now;
                var isInteractive = !string.Equals(payload.Source, "exec", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(payload.Originator, "codex_exec", StringComparison.OrdinalIgnoreCase);

                return new CodexSessionMeta(payload.Id, payload.Cwd ?? string.Empty, startedAt, string.Empty, isInteractive);
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to parse Codex session meta line: {ex.Message}");
            }
        }

        return null;
    }

    private Dictionary<string, CodexSessionMeta> GetCodexSessionMetaMap()
    {
        var result = new Dictionary<string, CodexSessionMeta>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_codexSessionsDir))
            return result;

        foreach (var path in Directory.GetFiles(_codexSessionsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            if (!TryReadFirstLineWithRetry(path, out var firstLine))
                continue;

            var meta = ParseCodexSessionMeta(firstLine);
            if (meta is null || string.IsNullOrWhiteSpace(meta.SessionId))
                continue;

            var withPath = meta with { FilePath = path };
            if (!result.TryGetValue(withPath.SessionId, out var current) || withPath.StartedAt < current.StartedAt)
                result[withPath.SessionId] = withPath;
        }

        return result;
    }

    private static CodexHistoryEntry? ParseCodexHistoryLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (!root.TryGetProperty("session_id", out var sessionIdElement))
            return null;

        var sessionId = sessionIdElement.GetString();
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        DateTime? timestamp = null;
        if (root.TryGetProperty("ts", out var tsElement) && tsElement.TryGetInt64(out var ts) && ts > 0)
            timestamp = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;

        string? text = null;
        if (root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            text = textElement.GetString();

        return new CodexHistoryEntry(sessionId, timestamp, text);
    }

    private Dictionary<string, LiveSessionProcess> MatchRunningCodexProcesses(
        IReadOnlyDictionary<string, CodexSessionMeta> metaById)
    {
        var result = new Dictionary<string, LiveSessionProcess>(StringComparer.OrdinalIgnoreCase);
        var candidates = GetRunningProcesses("codex")
            .OrderBy(p => p.StartTime ?? DateTime.MinValue)
            .ToList();

        foreach (var meta in metaById.Values.OrderByDescending(x => x.StartedAt))
        {
            var best = candidates
                .Select(p => new
                {
                    Process = p,
                    Delta = p.StartTime.HasValue ? (p.StartTime.Value - meta.StartedAt).Duration() : TimeSpan.MaxValue,
                })
                .Where(x => x.Delta <= TimeSpan.FromMinutes(2))
                .OrderBy(x => x.Delta)
                .FirstOrDefault();

            if (best is null)
                continue;

            candidates.Remove(best.Process);

            var pid = best.Process.Id;
            var windowHandle = WindowHelper.FindWindowForProcess(pid);
            result[meta.SessionId] = new LiveSessionProcess(
                pid,
                windowHandle,
                WindowHelper.GetEffectiveTitle(windowHandle, pid));
        }

        return result;
    }

    private static List<RunningProcessInfo> GetRunningProcesses(string processName)
    {
        var result = new List<RunningProcessInfo>();

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.HasExited)
                    continue;

                result.Add(new RunningProcessInfo(process.Id, process.StartTime));
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to inspect process '{processName}' ({process.Id}): {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    private string ResolveGeminiProjectPath(string sessionPath)
    {
        var chatDir = Path.GetDirectoryName(sessionPath);
        var projectDir = chatDir is null ? null : Directory.GetParent(chatDir)?.FullName;
        if (string.IsNullOrWhiteSpace(projectDir))
            return string.Empty;

        var markerPath = Path.Combine(projectDir, ".project_root");
        if (!TryReadAllTextWithRetry(markerPath, out var text))
            return string.Empty;

        return text.Trim();
    }

    public SessionPreviewData BuildPreview(SessionInfo session)
    {
        var title = $"{session.SourceLabel} / {session.ProjectName}";
        var subtitle = $"{session.ReferenceTime:G}  {session.ProjectPath}";
        var content = session.SourceApp switch
        {
            SessionSourceApp.Claude => BuildClaudePreview(session),
            SessionSourceApp.Codex => BuildCodexPreview(session),
            SessionSourceApp.Gemini => BuildGeminiPreview(session),
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(content))
            content = "No preview content was found for this session.";

        return new SessionPreviewData(title, subtitle, content);
    }

    private string BuildClaudePreview(SessionInfo session)
    {
        if (!TryReadAllTextWithRetry(_claudeHistoryPath, out var text))
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var line in EnumerateNonEmptyLines(text))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<ClaudeHistoryEntry>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entry is null || !string.Equals(entry.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(entry.Display))
                    continue;

                var stamp = entry.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).LocalDateTime.ToString("g")
                    : string.Empty;
                builder.AppendLine($"[{stamp}] user");
                builder.AppendLine(entry.Display.Trim());
                builder.AppendLine();
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to build Claude preview line: {ex.Message}");
            }
        }

        return TrimPreview(builder.ToString());
    }

    private string BuildCodexPreview(SessionInfo session)
    {
        var metaById = GetCodexSessionMetaMap();
        if (!metaById.TryGetValue(session.SessionId, out var meta))
            return string.Empty;
        if (!TryReadAllTextWithRetry(meta.FilePath, out var text))
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var line in EnumerateNonEmptyLines(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!TryGetProperty(root, "type", out var typeElement) ||
                    !string.Equals(typeElement.GetString(), "response_item", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!TryGetProperty(root, "payload", out var payload) ||
                    !TryGetProperty(payload, "type", out var payloadType) ||
                    !string.Equals(payloadType.GetString(), "message", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!TryGetProperty(payload, "role", out var roleElement))
                    continue;

                var role = roleElement.GetString();
                if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!TryGetProperty(payload, "content", out var contentElement) ||
                    contentElement.ValueKind != JsonValueKind.Array)
                    continue;

                var textParts = contentElement.EnumerateArray()
                    .Select(ReadCodexContentText)
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .ToList();

                if (textParts.Count == 0)
                    continue;

                builder.AppendLine(role);
                foreach (var part in textParts)
                    builder.AppendLine(part!);
                builder.AppendLine();
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to build Codex preview line: {ex.Message}");
            }
        }

        return TrimPreview(builder.ToString());
    }

    private string BuildGeminiPreview(SessionInfo session)
    {
        var latestPath = FindLatestGeminiSessionPath(session);
        if (string.IsNullOrWhiteSpace(latestPath) || !TryReadAllTextWithRetry(latestPath, out var text))
            return string.Empty;

        try
        {
            var data = JsonSerializer.Deserialize<GeminiSessionFileData>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (data?.Messages == null)
                return string.Empty;

            var builder = new StringBuilder();
            foreach (var message in data.Messages)
            {
                var role = string.IsNullOrWhiteSpace(message.Type) ? "message" : message.Type;
                var content = message.Content?
                    .Select(c => c.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (content == null || content.Count == 0)
                    continue;

                builder.AppendLine(role);
                foreach (var part in content)
                    builder.AppendLine(part!);
                builder.AppendLine();
            }

            return TrimPreview(builder.ToString());
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to build Gemini preview: {ex.Message}");
            return string.Empty;
        }
    }

    private string? FindLatestGeminiSessionPath(SessionInfo session)
    {
        if (!Directory.Exists(_geminiTmpDir))
            return null;

        var latestPath = default(string);
        var latestTime = DateTime.MinValue;

        foreach (var path in Directory.GetFiles(_geminiTmpDir, "session-*.json", SearchOption.AllDirectories))
        {
            var projectPath = ResolveGeminiProjectPath(path);
            if (!string.Equals(BuildGeminiProjectKey(projectPath, path), session.StorageKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp <= latestTime)
                continue;

            latestTime = stamp;
            latestPath = path;
        }

        return latestPath;
    }

    private static string? ReadCodexContentText(JsonElement element)
    {
        if (!TryGetProperty(element, "type", out var typeElement))
            return null;

        var type = typeElement.GetString();
        var text = type switch
        {
            "input_text" when TryGetProperty(element, "text", out var textElement) => textElement.GetString(),
            "output_text" when TryGetProperty(element, "text", out var outputTextElement) => outputTextElement.GetString(),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("<environment_context>", StringComparison.Ordinal) ||
            trimmed.StartsWith("<permissions instructions>", StringComparison.Ordinal) ||
            trimmed.StartsWith("<collaboration_mode>", StringComparison.Ordinal) ||
            trimmed.StartsWith("<apps_instructions>", StringComparison.Ordinal) ||
            trimmed.StartsWith("<skills_instructions>", StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value))
            return true;

        value = default;
        return false;
    }

    private static string TrimPreview(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        const int maxLength = 6000;
        var trimmed = content.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "\n...";
    }

    private static IEnumerable<string> EnumerateNonEmptyLines(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }

    private static bool TryReadAllTextWithRetry(string path, out string text, int maxAttempts = 5)
    {
        text = string.Empty;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
                return true;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                LogDebug($"Retrying read for '{path}' ({attempt}/{maxAttempts}): {ex.Message}");
                Thread.Sleep(100 * attempt);
            }
            catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
            {
                LogDebug($"Retrying read for '{path}' ({attempt}/{maxAttempts}): {ex.Message}");
                Thread.Sleep(100 * attempt);
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to read '{path}': {ex.Message}");
                break;
            }
        }

        return false;
    }

    private static bool TryReadFirstLineWithRetry(string path, out string line, int maxAttempts = 5)
    {
        line = string.Empty;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                line = reader.ReadLine() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(line);
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                LogDebug($"Retrying first-line read for '{path}' ({attempt}/{maxAttempts}): {ex.Message}");
                Thread.Sleep(100 * attempt);
            }
            catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
            {
                LogDebug($"Retrying first-line read for '{path}' ({attempt}/{maxAttempts}): {ex.Message}");
                Thread.Sleep(100 * attempt);
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to read first line from '{path}': {ex.Message}");
                break;
            }
        }

        return false;
    }

    private static T? ReadJsonOrDefault<T>(string path)
    {
        if (!TryReadAllTextWithRetry(path, out var text))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(text);
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to deserialize '{path}': {ex.Message}");
            return default;
        }
    }

    private static void AtomicWriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(value));
            File.Move(tmpPath, path, true);
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to write '{path}': {ex.Message}");
        }
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to inspect process liveness for PID {pid}: {ex.Message}");
            return false;
        }
    }

    private static DateTime? ParseLocalDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.LocalDateTime
            : null;
    }

    private static string BuildStorageKey(SessionSourceApp sourceApp, string sessionId)
        => $"{sourceApp}:{sessionId}";

    private static string BuildGeminiProjectKey(string projectPath, string sessionPath)
    {
        var normalized = string.IsNullOrWhiteSpace(projectPath)
            ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(sessionPath)) ?? string.Empty)
            : projectPath.Trim().ToLowerInvariant();
        return $"{SessionSourceApp.Gemini}:project:{normalized}";
    }

    private static void LogDebug(string message)
    {
        Debug.WriteLine($"[CSM] {message}");
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _elapsedTimer?.Dispose();
    }

    private sealed record PersistedSessionRecord(
        string? StorageKey,
        SessionSourceApp SourceApp,
        string SessionId,
        string? ProjectPath,
        DateTime StartedAt,
        DateTime? LastActivity,
        string? FirstMessage,
        bool IsManuallyArchived,
        bool IsDeleted);

    private sealed record DiscoveredSession(
        string StorageKey,
        SessionSourceApp SourceApp,
        string SessionId,
        string ProjectPath,
        DateTime StartedAt,
        DateTime? LastActivity,
        string? FirstMessage,
        bool IsAlive,
        int Pid,
        IntPtr WindowHandle,
        string WindowTitle);

    private sealed record ClaudeSessionFileData(
        int Pid,
        string SessionId,
        string? Cwd,
        long StartedAt);

    private sealed record ClaudeHistoryEntry(
        string SessionId,
        long Timestamp,
        string? Display);

    private sealed record ClaudeHistoryInfo(
        DateTime? LastActivity,
        string? FirstMessage);

    private sealed record CodexHistoryEntry(
        string SessionId,
        DateTime? Timestamp,
        string? Text)
    ;

    private sealed record CodexHistoryInfo(
        DateTime StartedAt,
        DateTime? LastActivity,
        string? FirstMessage);

    private sealed record CodexSessionMeta(
        string SessionId,
        string ProjectPath,
        DateTime StartedAt,
        string FilePath,
        bool IsInteractive);

    private sealed record CodexSessionLine(
        string? Type,
        CodexSessionPayload? Payload);

    private sealed record CodexSessionPayload(
        string? Id,
        string? Timestamp,
        string? Cwd,
        string? Source,
        string? Originator);

    private sealed record GeminiSessionFileData(
        string SessionId,
        string? StartTime,
        string? LastUpdated,
        List<GeminiMessage>? Messages);

    private sealed record GeminiMessage(
        string? Type,
        List<GeminiContent>? Content);

    private sealed record GeminiContent(
        string? Text);

    private sealed record GeminiDiscoveredSession(
        string StorageKey,
        string SessionId,
        string ProjectPath,
        DateTime StartedAt,
        DateTime LastActivity,
        string? FirstMessage);

    private sealed record RunningProcessInfo(
        int Id,
        DateTime? StartTime);

    private sealed record LiveSessionProcess(
        int Pid,
        IntPtr WindowHandle,
        string WindowTitle);
}
