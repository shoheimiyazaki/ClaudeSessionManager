using System.Collections.ObjectModel;
using System.Windows.Input;
using ClaudeSessionManager.Models;
using ClaudeSessionManager.Services;

namespace ClaudeSessionManager.ViewModels;

public sealed class MainViewModel
{
    private readonly SessionWatcher _watcher;

    public ObservableCollection<SessionInfo> Sessions => _watcher.Sessions;

    public ICommand FocusWindowCommand { get; }

    public MainViewModel()
    {
        _watcher = new SessionWatcher();
        _watcher.Start();

        FocusWindowCommand = new RelayCommand<SessionInfo>(session =>
        {
            if (session is null) return;
            if (session.WindowHandle != System.IntPtr.Zero)
                WindowHelper.FocusWindow(session.WindowHandle);
        });
    }
}
