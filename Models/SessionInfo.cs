using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ClaudeSessionManager.Models;

public class SessionInfo : INotifyPropertyChanged
{
    private bool _isAlive;
    private string _windowTitle = string.Empty;
    private IntPtr _windowHandle;
    private DateTime? _lastActivity;
    private bool _isArchived;
    private string? _customTitle;
    private string? _firstMessage;
    private bool _isEditingTitle;

    public int Pid { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string ProjectPath { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }

    public string ProjectName
    {
        get
        {
            var trimmed = ProjectPath.TrimEnd('/', '\\');
            return string.IsNullOrEmpty(trimmed) ? $"PID:{Pid}" : Path.GetFileName(trimmed);
        }
    }

    public bool IsAlive
    {
        get => _isAlive;
        set
        {
            if (_isAlive == value) return;
            _isAlive = value;
            OnPropertyChanged();
        }
    }

    public bool IsArchived
    {
        get => _isArchived;
        set
        {
            if (_isArchived == value) return;
            _isArchived = value;
            OnPropertyChanged();
        }
    }

    public string WindowTitle
    {
        get => _windowTitle;
        set
        {
            if (_windowTitle == value) return;
            _windowTitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleText));
        }
    }

    /// <summary>
    /// ユーザーがリネームで設定したカスタムタイトル。最優先で表示される。
    /// </summary>
    public string? CustomTitle
    {
        get => _customTitle;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_customTitle == normalized) return;
            _customTitle = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleText));
        }
    }

    /// <summary>
    /// history.jsonl から取得した最初のユーザーメッセージ。セッション内容のヒントとして表示する。
    /// </summary>
    public string? FirstMessage
    {
        get => _firstMessage;
        set
        {
            if (_firstMessage == value) return;
            _firstMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleText));
        }
    }

    /// <summary>UI で現在リネーム編集中かどうか。</summary>
    public bool IsEditingTitle
    {
        get => _isEditingTitle;
        set
        {
            if (_isEditingTitle == value) return;
            _isEditingTitle = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// タイトル下に表示する補助情報。
    /// 優先順位: CustomTitle(ユーザー指定) → FirstMessage(履歴の最初のプロンプト) → WindowTitle → 空。
    /// PID は冗長なので表示しない(識別用途は ProjectName と FirstMessage で十分)。
    /// </summary>
    public string SubtitleText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customTitle))
                return _customTitle!;
            if (!string.IsNullOrWhiteSpace(_firstMessage))
                return TruncateSingleLine(_firstMessage!, 80);
            if (!string.IsNullOrWhiteSpace(_windowTitle))
                return _windowTitle;
            return string.Empty;
        }
    }

    private static string TruncateSingleLine(string s, int max)
    {
        var single = s.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
        if (single.Length <= max) return single;
        return single.Substring(0, max) + "…";
    }

    public IntPtr WindowHandle
    {
        get => _windowHandle;
        set
        {
            if (_windowHandle == value) return;
            _windowHandle = value;
            OnPropertyChanged();
        }
    }

    public DateTime? LastActivity
    {
        get => _lastActivity;
        set
        {
            if (_lastActivity == value) return;
            _lastActivity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastActivityText));
            OnPropertyChanged(nameof(SortKey));
        }
    }

    /// <summary>ソートキー。LastActivity が null の場合は StartedAt を使う。</summary>
    public DateTime SortKey => LastActivity ?? StartedAt;

    /// <summary>アーカイブ判定の基準となる「最後の活動時刻」。履歴がなければ StartedAt。</summary>
    public DateTime ReferenceTime => LastActivity ?? StartedAt;

    public string LastActivityText
    {
        get
        {
            if (_lastActivity == null) return string.Empty;
            var elapsed = DateTime.Now - _lastActivity.Value;
            if (elapsed.TotalDays >= 1)   return $"{(int)elapsed.TotalDays}d前";
            if (elapsed.TotalHours >= 1)  return $"{(int)elapsed.TotalHours}h{elapsed.Minutes:D2}m前";
            if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes}m前";
            return "今";
        }
    }

    public string ElapsedTime
    {
        get
        {
            var elapsed = DateTime.Now - StartedAt;
            if (elapsed.TotalHours >= 1)  return $"{(int)elapsed.TotalHours}h{elapsed.Minutes:D2}m";
            if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes}m";
            return "今";
        }
    }

    public void NotifyTimesChanged()
    {
        OnPropertyChanged(nameof(ElapsedTime));
        OnPropertyChanged(nameof(LastActivityText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
