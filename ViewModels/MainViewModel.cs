using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using ClaudeSessionManager.Models;
using ClaudeSessionManager.Services;

namespace ClaudeSessionManager.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SessionWatcher _watcher;
    private readonly ListCollectionView _activeView;
    private readonly ListCollectionView _archivedView;
    private int _selectedTab;

    /// <summary>現在表示中のビュー（タブに応じて切替）</summary>
    public System.Collections.IEnumerable CurrentSessions =>
        _selectedTab == 0 ? (System.Collections.IEnumerable)_activeView : _archivedView;

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab == value) return;
            _selectedTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentSessions));
        }
    }

    public ICommand FocusOrResumeCommand     { get; }
    public ICommand ArchiveCommand           { get; }
    public ICommand RestoreCommand           { get; }
    public ICommand SelectActiveTabCommand   { get; }
    public ICommand SelectArchivedTabCommand { get; }

    public MainViewModel()
    {
        _watcher = new SessionWatcher();

        _activeView = new ListCollectionView(_watcher.Sessions)
        {
            Filter = o => o is SessionInfo s && !s.IsArchived,
        };
        _activeView.SortDescriptions.Add(
            new SortDescription(nameof(SessionInfo.SortKey), ListSortDirection.Descending));

        _archivedView = new ListCollectionView(_watcher.Sessions)
        {
            Filter = o => o is SessionInfo s && s.IsArchived,
        };
        _archivedView.SortDescriptions.Add(
            new SortDescription(nameof(SessionInfo.SortKey), ListSortDirection.Descending));

        _watcher.DataRefreshed += () =>
        {
            _activeView.Refresh();
            _archivedView.Refresh();
        };

        _watcher.Start();

        FocusOrResumeCommand = new RelayCommand<SessionInfo>(OnFocusOrResume);

        ArchiveCommand = new RelayCommand<SessionInfo>(session =>
        {
            if (session is null) return;
            session.IsArchived = true;
            _watcher.SaveArchivedIds();
            _activeView.Refresh();
            _archivedView.Refresh();
        });

        RestoreCommand = new RelayCommand<SessionInfo>(session =>
        {
            if (session is null) return;
            session.IsArchived = false;
            _watcher.SaveArchivedIds();
            _activeView.Refresh();
            _archivedView.Refresh();
        });

        SelectActiveTabCommand   = new RelayCommand<object>(_ => SelectedTab = 0);
        SelectArchivedTabCommand = new RelayCommand<object>(_ => SelectedTab = 1);
    }

    private static void OnFocusOrResume(SessionInfo? session)
    {
        if (session is null) return;

        if (session.IsAlive && session.WindowHandle != IntPtr.Zero)
        {
            WindowHelper.FocusWindow(session.WindowHandle);
            return;
        }

        if (!session.IsAlive && !string.IsNullOrEmpty(session.SessionId))
        {
            var setLoc = string.IsNullOrEmpty(session.ProjectPath)
                ? string.Empty
                : $"Set-Location '{session.ProjectPath.Replace("'", "''")}'; ";

            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments       = $"-NoExit -Command \"{setLoc}claude --resume {session.SessionId}\"",
                UseShellExecute = true,
            });
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
