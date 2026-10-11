using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GameTracker.Desktop;

internal static class WindowBounds
{
    public static void Attach(Window window)
    {
        HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(
            (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) => AdjustMaximizedBounds(window, hwnd, message, lParam, ref handled));
    }

    private static IntPtr AdjustMaximizedBounds(Window window, IntPtr hwnd, int message, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO
        var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var bounds = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref bounds)) return IntPtr.Zero;
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // Use this monitor's work area in physical pixels, including taskbars on
        // secondary displays. WPF dimensions are DIPs; native bounds are pixels.
        info.MaxPosition = new NativePoint { X = bounds.Work.Left - bounds.Monitor.Left, Y = bounds.Work.Top - bounds.Monitor.Top };
        info.MaxSize = new NativePoint { X = bounds.Work.Right - bounds.Work.Left, Y = bounds.Work.Bottom - bounds.Work.Top };
        var dpi = VisualTreeHelper.GetDpi(window);
        info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX));
        info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY));
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
