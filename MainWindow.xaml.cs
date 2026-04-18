using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClaudeSessionManager.Models;
using ClaudeSessionManager.Services;
using ClaudeSessionManager.ViewModels;

namespace ClaudeSessionManager;

public partial class MainWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    // --- シングル/ダブルクリック判定用 ---
    //
    // WPF の Button は Click を常に MouseUp で発火するため、ダブルクリックだと
    // 「1回目 Click → 2回目 Click (+MouseDoubleClick)」の順で発火してしまう。
    // ダブルクリック時に FocusOrResume が発火するのを防ぐため、
    // 1回目の Click をタイマーで遅延させ、タイマー満了前に2回目が来たらリネーム扱いとする。
    private DispatcherTimer? _clickTimer;
    private SessionInfo? _pendingClickSession;

    public MainWindow()
    {
        InitializeComponent();
        Loaded          += OnLoaded;
        ContentRendered += OnContentRendered;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // WS_EX_TOOLWINDOW を設定する:
        //   - タスクバー / Alt+Tab / Win+Tab (タスクビュー) から除外される
        //   - Topmost と組み合わせることで全仮想デスクトップに表示される可能性が高まる
        var hwnd    = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        PositionToBottomRight();
        // COM API (IVirtualDesktopPinnedApps) でのピン留めも試みる
        // Win11 25H2 ではGUIDが変わっている可能性があり失敗してもサイレントに無視する
        VirtualDesktopHelper.PinWindowToAllDesktops(this);
    }

    private void PositionToBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top  = area.Bottom - ActualHeight - 16;
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    // ---- セッション行のクリック/ダブルクリック制御 ----

    private void SessionItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not SessionInfo session) return;

        // 同一セッションへの2連続クリックをダブルクリックと見なす。
        if (_clickTimer != null && ReferenceEquals(_pendingClickSession, session))
        {
            // ダブルクリック: シングルクリックのコマンド発火をキャンセルし、リネームモードへ。
            _clickTimer.Stop();
            _clickTimer = null;
            _pendingClickSession = null;
            session.IsEditingTitle = true;
            return;
        }

        // シングルクリック: OS のダブルクリック時間だけ待って、来なければコマンド発火。
        _pendingClickSession = session;
        _clickTimer?.Stop();
        var interval = TimeSpan.FromMilliseconds(Math.Max(200, GetDoubleClickTime()));
        _clickTimer = new DispatcherTimer { Interval = interval };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer?.Stop();
            _clickTimer = null;
            var target = _pendingClickSession;
            _pendingClickSession = null;
            if (target == null) return;
            if (DataContext is MainViewModel vm)
                vm.FocusOrResumeCommand.Execute(target);
        };
        _clickTimer.Start();
    }

    // ---- リネーム TextBox 制御 ----

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (!(bool)e.NewValue) return;

        // 可視化直後にフォーカスを与えテキスト全選択。
        // レイアウト確定後に実行するため Dispatcher.BeginInvoke で後追いする。
        tb.Dispatcher.BeginInvoke(new Action(() =>
        {
            tb.Focus();
            Keyboard.Focus(tb);
            tb.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not SessionInfo session) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitRename(tb, session);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // 編集キャンセル: バインディングを元に戻す。
            var expr = tb.GetBindingExpression(TextBox.TextProperty);
            expr?.UpdateTarget();
            session.IsEditingTitle = false;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not SessionInfo session) return;
        if (!session.IsEditingTitle) return; // 既に Enter/Esc で抜けている場合
        CommitRename(tb, session);
    }

    private void CommitRename(TextBox tb, SessionInfo session)
    {
        var expr = tb.GetBindingExpression(TextBox.TextProperty);
        expr?.UpdateSource();
        session.IsEditingTitle = false;
        if (DataContext is MainViewModel vm)
            vm.PersistCustomTitles();
    }
}
