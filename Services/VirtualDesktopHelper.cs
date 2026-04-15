using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeSessionManager.Services;

/// <summary>
/// WPF ウィンドウを全仮想デスクトップに固定するヘルパー。
/// Windows 11 の非公式 COM API (IVirtualDesktopPinnedApps) を使用する。
/// 失敗してもサイレントに無視する（バージョン非互換のため）。
/// </summary>
public static class VirtualDesktopHelper
{
    // ImmersiveShell の CLSID（Win10/11 共通）
    private static readonly Guid CLSID_ImmersiveShell =
        new("C2F03A33-21F5-47FA-B4BB-156362A2F239");

    // IVirtualDesktopPinnedApps の SID/IID
    // Win11 21H2-22H2 (Build 22000-22621)
    private static readonly Guid IID_PinnedApps_Win11 =
        new("4CE81583-1E4C-4632-A621-07A53543148F");

    /// <summary>ウィンドウを全仮想デスクトップに固定する</summary>
    public static void PinWindowToAllDesktops(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            TryPinWindow(hwnd);
        }
        catch
        {
            // バージョン非互換の場合はサイレントに失敗
        }
    }

    private static void TryPinWindow(IntPtr hwnd)
    {
        // ImmersiveShell COM オブジェクトを作成
        Type? shellType = Type.GetTypeFromCLSID(CLSID_ImmersiveShell, throwOnError: false);
        if (shellType == null) return;

        object? shellObj = Activator.CreateInstance(shellType);
        if (shellObj is not IShellServiceProvider sp) return;

        // IVirtualDesktopPinnedApps を QueryService で取得
        var iid = IID_PinnedApps_Win11;
        sp.QueryService(ref iid, ref iid, out object pinnedAppsObj);

        if (pinnedAppsObj is IVirtualDesktopPinnedApps pinnedApps)
        {
            pinnedApps.PinWindow(hwnd);
        }
    }

    // ---- COM インターフェース定義 ----

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        void QueryService(
            ref Guid guidService,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport]
    [Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedApps
    {
        // IsAppIdPinned
        [PreserveSig] int IsAppIdPinned(
            [MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.Bool)] out bool isPinned);
        // PinAppID
        [PreserveSig] int PinAppID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);
        // UnpinAppID
        [PreserveSig] int UnpinAppID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);
        // IsViewPinned
        [PreserveSig] int IsViewPinned(
            [MarshalAs(UnmanagedType.IUnknown)] object view,
            [MarshalAs(UnmanagedType.Bool)] out bool isPinned);
        // PinView
        [PreserveSig] int PinView(
            [MarshalAs(UnmanagedType.IUnknown)] object view);
        // UnpinView
        [PreserveSig] int UnpinView(
            [MarshalAs(UnmanagedType.IUnknown)] object view);
        // IsWindowPinned
        [PreserveSig] int IsWindowPinned(
            IntPtr hWnd,
            [MarshalAs(UnmanagedType.Bool)] out bool isPinned);
        // PinWindow
        [PreserveSig] int PinWindow(IntPtr hWnd);
        // UnpinWindow
        [PreserveSig] int UnpinWindow(IntPtr hWnd);
    }
}
