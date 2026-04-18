using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

    /// <summary>現在表示中のビュー(タブに応じて切替)</summary>
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
    public ICommand BeginRenameCommand       { get; }

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
            // 24h ルールで即再アーカイブされないよう、このセッションを自動アーカイブ対象外にする。
            _watcher.MarkExemptFromAutoArchive(session.SessionId);
            _watcher.SaveArchivedIds();
            _activeView.Refresh();
            _archivedView.Refresh();
        });

        SelectActiveTabCommand   = new RelayCommand<object>(_ => SelectedTab = 0);
        SelectArchivedTabCommand = new RelayCommand<object>(_ => SelectedTab = 1);

        BeginRenameCommand = new RelayCommand<SessionInfo>(session =>
        {
            if (session is null) return;
            session.IsEditingTitle = true;
        });
    }

    /// <summary>
    /// カスタムタイトルをファイルに永続化する(コードビハインドから呼び出される)。
    /// </summary>
    public void PersistCustomTitles() => _watcher.SaveCustomTitles();

    /// <summary>
    /// セッション ID 形式検証用。UUID v4 形式に一致しない値はシェル引数として展開しない。
    /// (session JSON は外部ファイルなので、任意値 + PowerShell 引数連結はコマンドインジェクションになりうる)
    /// </summary>
    private static readonly Regex SessionIdPattern =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

    /// <summary>
    /// セッション行がクリックされたときの処理。
    ///   - 生存 & ウィンドウハンドル取得済み → そのウィンドウをフォアグラウンドに
    ///   - それ以外(dead もしくは親ターミナルが特定できない「孤立プロセス」) → claude --resume で再開
    /// これにより「生存しているがウィンドウが見つからず何も起きない」状態を解消する。
    /// </summary>
    private static void OnFocusOrResume(SessionInfo? session)
    {
        if (session is null) return;

        if (session.IsAlive && session.WindowHandle != IntPtr.Zero)
        {
            WindowHelper.FocusWindow(session.WindowHandle);
            return;
        }

        // dead 又は 孤立 (alive だがターミナルに到達できない)
        if (string.IsNullOrEmpty(session.SessionId)) return;

        // セッション ID は外部ファイル由来。UUID 形式以外はコマンドインジェクションになりうるので拒否。
        if (!SessionIdPattern.IsMatch(session.SessionId)) return;

        // ProjectPath も外部ファイル由来。WorkingDirectory で渡すことで
        // PowerShell のコマンド文字列に展開せず、文字列エスケープ不具合のリスクを回避する。
        var workingDir = !string.IsNullOrEmpty(session.ProjectPath)
                         && Directory.Exists(session.ProjectPath)
            ? session.ProjectPath
            : string.Empty;

        // ArgumentList で個別引数として渡す (文字列連結しない)。
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute  = true,
            WorkingDirectory = workingDir,
        };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add($"claude --resume {session.SessionId}");
        Process.Start(psi);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
