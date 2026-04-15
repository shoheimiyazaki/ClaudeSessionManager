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
    /// タイトル下に表示する補助情報。
    /// WindowTitle が信頼できる場合のみ表示、なければ PID を表示して
    /// 同一プロジェクトの複数セッションを区別できるようにする。
    /// </summary>
    public string SubtitleText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_windowTitle))
                return _windowTitle;
            return $"PID {Pid}";
        }
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

    /// <summary>ソートキー。LastActivity が null の場合は最下位（DateTime.MinValue）</summary>
    public DateTime SortKey => LastActivity ?? DateTime.MinValue;

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
