using System.Runtime.InteropServices;

internal static class NativeWindows
{
    private delegate bool EnumCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLong(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    internal static nint Find(int pid)
    {
        nint found = 0;
        EnumCallback callback = (window, _) =>
        {
            GetWindowThreadProcessId(window, out var process);
            if (process == pid && IsWindowVisible(window) && GetWindow(window, 4) == 0) { found = window; return false; }
            return true;
        };
        EnumWindows(callback, 0); GC.KeepAlive(callback); return found;
    }
    internal static bool Topmost(nint window) => ((long)GetWindowLong(window, -20) & 8) != 0;
    internal static uint Owner(nint window) { GetWindowThreadProcessId(window, out var process); return process; }
    internal static byte Alpha(nint window)
    {
        if (((long)GetWindowLong(window, -20) & 0x80000) == 0) return 255;
        if (!GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) || flags != 2) throw new Exception("could not read layered alpha");
        return alpha;
    }
    internal static bool Above(nint upper, nint lower)
    {
        for (var next = GetWindow(lower, 3); next != 0; next = GetWindow(next, 3)) if (next == upper) return true;
        return false;
    }
    internal static nint Overlay() => CreateWindowExW(0x08000088, "STATIC", "CRER integration owned overlay", 0x90000000, 30, 30, 400, 300, 0, 0, 0, 0);
    internal static (Rect Rect, uint Dpi, int Awareness) Measure(nint window)
    {
        var previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            if (!GetWindowRect(window, out var rect)) throw new Exception("GetWindowRect failed");
            return (rect, GetDpiForWindow(window), GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(window)));
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    internal static Point Cursor() { if (!GetCursorPos(out var point)) throw new Exception("GetCursorPos failed"); return point; }
    internal static void SetOwnedAlpha(nint window, byte alpha)
    {
        SetWindowLong(window, -20, (nint)((long)GetWindowLong(window, -20) | 0x80000));
        if (!SetLayeredWindowAttributes(window, 0, alpha, 2)) throw new Exception("owned probe opacity could not be applied");
    }
}
