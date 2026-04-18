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

    public int Pid { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string ProjectPath { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public string SourceFilePath { get; init; } = string.Empty;

    public string ProjectName
    {
        get
        {
            var trimmed = ProjectPath.TrimEnd('/', '\\');
            return string.IsNullOrEmpty(trimmed)
                ? $"PID:{Pid}"
                : Path.GetFileName(trimmed);
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
            OnPropertyChanged(nameof(StatusLabel));
        }
    }

    public string StatusLabel => _isAlive ? "Running" : "Closed";

    public string WindowTitle
    {
        get => _windowTitle;
        set
        {
            if (_windowTitle == value) return;
            _windowTitle = value;
            OnPropertyChanged();
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
        }
    }

    public string LastActivityText
    {
        get
        {
            if (_lastActivity == null) return string.Empty;

            var elapsed = DateTime.Now - _lastActivity.Value;
            if (elapsed.TotalDays >= 1)
                return $"{(int)elapsed.TotalDays}d ago";
            if (elapsed.TotalHours >= 1)
                return $"{(int)elapsed.TotalHours}h{elapsed.Minutes:D2}m ago";
            if (elapsed.TotalMinutes >= 1)
                return $"{(int)elapsed.TotalMinutes}m ago";
            return "now";
        }
    }

    public string ElapsedTime
    {
        get
        {
            var elapsed = DateTime.Now - StartedAt;
            if (elapsed.TotalHours >= 1)
                return $"{(int)elapsed.TotalHours}h{elapsed.Minutes:D2}m";
            if (elapsed.TotalMinutes >= 1)
                return $"{(int)elapsed.TotalMinutes}m";
            return "now";
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
