using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ClaudeSessionManager.Models;
using ClaudeSessionManager.Services;

namespace ClaudeSessionManager.ViewModels;

public sealed class MainViewModel : IDisposable
{
    private readonly SessionWatcher _watcher;
    private bool _disposed;

    public ObservableCollection<SessionInfo> Sessions => _watcher.Sessions;

    public ICommand FocusWindowCommand { get; }

    public MainViewModel()
    {
        _watcher = new SessionWatcher();
        _watcher.Start();

        FocusWindowCommand = new RelayCommand<SessionInfo>(session =>
        {
            if (session is null) return;
            if (session.WindowHandle != IntPtr.Zero)
                WindowHelper.FocusWindow(session.WindowHandle);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Dispose();
    }
}
