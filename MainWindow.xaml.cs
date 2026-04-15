using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ClaudeSessionManager.Services;

namespace ClaudeSessionManager;

public partial class MainWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

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
}
