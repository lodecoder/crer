using System;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Crer.WindowsTests")]

namespace Crer.Windows;

internal static class InputBridge
{
    private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RI_MOUSE_LEFT_BUTTON_DOWN = 1, RI_MOUSE_LEFT_BUTTON_UP = 2, RI_MOUSE_WHEEL = 0x400;
    private static readonly ConcurrentQueue<InputEvent> Queue = new();
    // EnumWindows のコールバックは対象フィールドを共有するため、全呼び出し元の処理を直列化する。
    private static readonly object WindowGate = new();
    private static readonly AutoResetEvent Stopped = new(false);
    private static readonly byte[] KeyboardState = new byte[256];
    private static readonly bool[] AcceptedModifiers = new bool[256];
    private static bool _leftButtonAccepted;
    private static Rect? _contentRelativeRect;
    private static Thread? _thread; private static volatile bool _running; private static uint _pid; private static volatile int _error; private static IntPtr _target, _content, _mouseHook, _keyboardHook; private static uint _threadId;
    private static volatile int _testHookFailure, _testStopTimeout;
    private static uint _foregroundPid; private static IntPtr _foregroundTarget; private static int _lastForegroundStatus;
    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader { public uint Type, Size; public IntPtr Device; public IntPtr WParam; }
    [StructLayout(LayoutKind.Explicit)] private struct RawInput { [FieldOffset(0)] public RawInputHeader Header; [FieldOffset(24)] public RawMouse Mouse; [FieldOffset(24)] public RawKeyboard Keyboard; }
    [StructLayout(LayoutKind.Explicit, Size=24)] private struct RawMouse {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawKeyboard { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr Hwnd; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Pt; }
    [StructLayout(LayoutKind.Sequential)] private struct LowLevelMouse { public Point Point; public uint MouseData, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct LowLevelKeyboard { public uint VKey, ScanCode, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [DllImport("user32.dll", SetLastError=true)] private static extern bool RegisterRawInputDevices(RawInputDevice[] d, uint n, uint cb);
    [DllImport("user32.dll")] private static extern int GetRawInputData(IntPtr h, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, [Out] char[] text, int textLength, uint flags, IntPtr keyboardLayout);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr h, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint command);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool GetLayeredWindowAttributes(IntPtr h, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, char[] name, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Point point);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern ushort RegisterClassW(ref WndClass c);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint e,string c,string n,uint s,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc p, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, UIntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, UIntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h,uint m,UIntPtr w,IntPtr l);
    private delegate IntPtr WndProc(IntPtr h,uint m,UIntPtr w,IntPtr l);
    private delegate IntPtr HookProc(int code, UIntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct WndClass { public uint Style; public WndProc Proc; public int ClsExtra, WndExtra; public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string Name; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? n);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long n);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceFrequency(out long n);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static readonly HookProc MouseHookProc = MouseHook;
    private static readonly HookProc KeyboardHookProc = KeyboardHook;
    private static readonly WndProc WindowProc = Proc;
    private static bool Find(IntPtr h, IntPtr _)
    {
        GetWindowThreadProcessId(h, out var p);
        if (p != _pid) return true;
        if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true;
        var name = new char[256];
        if (GetClassNameW(h, name, name.Length) == 0 ||
            !new string(name).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) return true;
        _target = h;
        return false;
    }
    private static bool FindForeground(IntPtr h, IntPtr _)
    {
        GetWindowThreadProcessId(h, out var p);
        if (p != _foregroundPid) return true;
        // Chrome menus/tooltips use the same widget class and precede their owner in Z order.
        // Making only an owned popup topmost reports success but leaves the browser underneath.
        if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true; // GW_OWNER
        var name = new char[256];
        if (GetClassNameW(h, name, name.Length) == 0 ||
            !new string(name).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) return true;
        _foregroundTarget = h;
        return false;
    }
    private static bool FindContent(IntPtr h, IntPtr output)
    {
        var name = new char[256];
        if (GetClassNameW(h, name, name.Length) > 0 && new string(name).StartsWith("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal))
        {
            if (!IsWindowVisible(h) || !PhysicalWindowRect(h, out var rect) || rect.Right - rect.Left < 32 || rect.Bottom - rect.Top < 32) return true;
            // ページの再生成中に残った DevTools を代わりに選ばない。
            if (_contentRelativeRect is { } expected && (!PhysicalWindowRect(_target, out var browser)
                || rect.Left - browser.Left != expected.Left || rect.Top - browser.Top != expected.Top
                || rect.Right - browser.Left != expected.Right || rect.Bottom - browser.Top != expected.Bottom)) return true;
            Marshal.WriteIntPtr(output, h);
            return false;
        }
        return true;
    }
    private static bool IsContentSurface(IntPtr h)
    {
        var name = new char[256];
        if (GetClassNameW(h, name, name.Length) == 0) return false;
        var className = new string(name);
        return className.StartsWith("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal) ||
            className.StartsWith("Intermediate D3D Window", StringComparison.Ordinal);
    }
    private static bool IsPageSurface(IntPtr window)
    {
        if (!IsContentSurface(window)) return false;
        var root = GetAncestor(window, 2); // GA_ROOT
        if (root == _target) return true;
        var name = new char[256];
        // D3D だけは独立した root を使う。別ウィンドウの DevTools は矩形が重なっても除く。
        return GetClassNameW(root, name, name.Length) > 0
            && new string(name).StartsWith("Intermediate D3D Window", StringComparison.Ordinal)
            && GetAncestor(window, 3) == _target;
    }
    private static bool PhysicalWindowRect(IntPtr window, out Rect rect)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try { return GetWindowRect(window, out rect); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    private static bool Active(bool mouse, Point? eventPoint = null)
    {
        lock (WindowGate)
        {
            var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                var point = eventPoint ?? GetPoint();
                var window = mouse ? WindowFromPoint(point) : GetForegroundWindow();
                return ActiveWindow(mouse, window, point);
            }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }
    }
    private static bool ActiveWindow(bool mouse, IntPtr window, Point point)
    {
        // D3D の中間ウィンドウは独立した root でも、owner をたどると対象ブラウザーになる。
        var root = GetAncestor(window, 3); // GA_ROOTOWNER
        if (root == IntPtr.Zero) return false;
        GetWindowThreadProcessId(root, out var pid);
        if (pid != _pid || !TryContentRect(out var content) || root != _target) return false;
        if (mouse)
            return Contains(content, point) && (window == _content || IsChild(_content, window) || IsPageSurface(window));

        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        var thread = GetWindowThreadProcessId(window, out _);
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero) return false;
        if (info.Focus == _content || IsChild(_content, info.Focus)) return true;
        // iframe や D3D の別 HWND はページの矩形内だけを許可し、隣接する DevTools は除く。
        return IsPageSurface(info.Focus) && PhysicalWindowRect(info.Focus, out var focus)
            && focus.Left >= content.Left && focus.Top >= content.Top && focus.Right <= content.Right && focus.Bottom <= content.Bottom;
    }
    private static bool Contains(Rect rect, Point point) => point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    private static bool TryContentRect(out Rect rect)
    {
        if (_target != IntPtr.Zero && !IsWindow(_target)) _target = _content = IntPtr.Zero;
        if (_content != IntPtr.Zero && IsWindowVisible(_content) && PhysicalWindowRect(_content, out rect)
            && rect.Right - rect.Left >= 32 && rect.Bottom - rect.Top >= 32)
        {
            RememberContentRect(rect);
            return true;
        }
        _content = IntPtr.Zero;
        if (_target == IntPtr.Zero) EnumWindows(Find, IntPtr.Zero);
        if (_target != IntPtr.Zero)
        {
            var handle = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(handle, IntPtr.Zero);
                EnumChildWindows(_target, FindContent, handle);
                _content = Marshal.ReadIntPtr(handle);
            }
            finally { Marshal.FreeHGlobal(handle); }
            if (_content != IntPtr.Zero && PhysicalWindowRect(_content, out rect)) { RememberContentRect(rect); return true; }
        }
        rect = default;
        return false;
    }
    private static void RememberContentRect(Rect rect)
    {
        if (PhysicalWindowRect(_target, out var browser))
            _contentRelativeRect = new Rect { Left = rect.Left - browser.Left, Top = rect.Top - browser.Top,
                Right = rect.Right - browser.Left, Bottom = rect.Bottom - browser.Top };
    }
    private static Point GetPoint(){ GetPhysicalCursorPos(out var p); return p; }
    private static void Fail(int error)
    {
        _error = error;
        _running = false;
        if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
    }
    private static void Push(uint kind, uint data = 0, Point? eventPoint = null)
    {
        if (Queue.Count >= 8192) { Fail(111); return; }
        QueryPerformanceCounter(out var q);
        var point = eventPoint ?? GetPoint();
        var input = new InputEvent { Qpc = (ulong)q, X = point.X, Y = point.Y, Kind = kind, Data = data };
        lock (WindowGate)
        {
            // キューの読取り前に窓が移動しても、採取時点のページ原点から座標を変換できるようにする。
            if (_target != IntPtr.Zero && TryContentRect(out var content))
            {
                input.HasContentBounds = true;
                input.ContentX = content.Left; input.ContentY = content.Top;
                input.ContentWidth = content.Right - content.Left; input.ContentHeight = content.Bottom - content.Top;
            }
        }
        Queue.Enqueue(input);
    }
    private static IntPtr MouseHook(int code, UIntPtr w, IntPtr l)
    {
        if (code >= 0) {
            var input = Marshal.PtrToStructure<LowLevelMouse>(l);
            if (!Active(true, input.Point)) return CallNextHookEx(_mouseHook, code, w, l);
            switch ((uint)w) {
                case 0x0200: Push(1, 0, input.Point); break;
                case 0x0201: Push(2, 0, input.Point); break;
                case 0x0202: Push(3, 0, input.Point); break;
                case 0x020A: Push(6, input.MouseData, input.Point); break;
            }
        }
        return CallNextHookEx(_mouseHook, code, w, l);
    }
    private static IntPtr KeyboardHook(int code, UIntPtr w, IntPtr l)
    {
        if (code >= 0) RecordKeyboard(Marshal.PtrToStructure<LowLevelKeyboard>(l), (uint)w);
        return CallNextHookEx(_keyboardHook, code, w, l);
    }
    private static void RecordKeyboard(LowLevelKeyboard input, uint message, IntPtr? foreground = null)
    {
        var down = message is 0x0100 or 0x0104;
        if (!down && message is not (0x0101 or 0x0105)) return;
        // 別アプリでの Caps Lock 切替や修飾キーの解放も追跡し、対象へ戻った際の状態を保つ。
        UpdateKeyboardState(input.VKey, down);
        var modifier = input.VKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
        // 対象内で受理した修飾キーだけは、別アプリへ移った後の解放も対として残す。
        // 対象外で始まった入力は記録せず、再訪時に以前の修飾キーが押下中として残ることを防ぐ。
        if (!down && modifier && AcceptedModifiers[input.VKey])
        {
            AcceptedModifiers[input.VKey] = false;
            Push(8, (input.VKey << 16) | input.ScanCode);
            return;
        }
        bool active;
        if (foreground is { } window) { lock (WindowGate) active = ActiveWindow(false, window, default); }
        else active = Active(false);
        if (!active) return;
        if (down && modifier) AcceptedModifiers[input.VKey] = true;
        if (!down) Push(8, (input.VKey << 16) | input.ScanCode);
        else if (TryCharacter(input, out var character)) Push(9, character);
        else Push(7, (input.VKey << 16) | input.ScanCode);
    }
    private static bool TryCharacter(LowLevelKeyboard input, out uint character)
    {
        character = 0;
        var text = new char[4];
        var count = ToUnicodeEx(input.VKey, input.ScanCode, KeyboardState, text, text.Length, 0, GetKeyboardLayout(0));
        if (count != 1 || char.IsControl(text[0])) return false;
        character = text[0];
        return true;
    }
    private static void UpdateKeyboardState(uint key, bool down)
    {
        if (key >= KeyboardState.Length) return;
        var previous = KeyboardState[key];
        var toggled = previous & 1;
        if (key == 0x14 && down && (previous & 0x80) == 0) toggled ^= 1;
        KeyboardState[key] = (byte)(toggled | (down ? 0x80 : 0));
        if (key is 0xA0 or 0xA1) {
            KeyboardState[0x10] = (KeyboardState[0xA0] & 0x80) != 0 || (KeyboardState[0xA1] & 0x80) != 0
                ? (byte)0x80 : (byte)0;
        }
        if (key is 0xA2 or 0xA3) {
            KeyboardState[0x11] = (KeyboardState[0xA2] & 0x80) != 0 || (KeyboardState[0xA3] & 0x80) != 0
                ? (byte)0x80 : (byte)0;
        }
        if (key is 0xA4 or 0xA5) {
            KeyboardState[0x12] = (KeyboardState[0xA4] & 0x80) != 0 || (KeyboardState[0xA5] & 0x80) != 0
                ? (byte)0x80 : (byte)0;
        }
    }
    private static void InitializeKeyboardState(short capsState, short capsAsyncState)
    {
        Array.Clear(KeyboardState);
        Array.Clear(AcceptedModifiers);
        KeyboardState[0x14] = (byte)((capsState & 1) | (capsAsyncState < 0 ? 0x80 : 0));
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            KeyboardState[key] = (byte)(GetAsyncKeyState(key) < 0 ? 0x80 : 0);
    }
    private static void RecordMouse(RawMouse mouse, Point point)
    {
        if (mouse.LastX != 0 || mouse.LastY != 0) Push(1, 0, point);
        if ((mouse.ButtonFlags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) Push(2, 0, point);
        if ((mouse.ButtonFlags & RI_MOUSE_LEFT_BUTTON_UP) != 0) Push(3, 0, point);
        if ((mouse.ButtonFlags & RI_MOUSE_WHEEL) != 0) Push(6, mouse.ButtonData, point);
    }
    private static void RecordTargetMouse(RawMouse mouse, Point point)
    {
        var active = Active(true, point);
        if ((mouse.ButtonFlags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) _leftButtonAccepted = active;
        var flags = mouse.ButtonFlags;
        if (!_leftButtonAccepted) mouse.ButtonFlags &= unchecked((ushort)~RI_MOUSE_LEFT_BUTTON_UP);
        if (active) RecordMouse(mouse, point);
        if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0) _leftButtonAccepted = false;
    }
    private static IntPtr Proc(IntPtr h, uint m, UIntPtr w, IntPtr l)
    {
        if (m != 0x00FF) return DefWindowProcW(h, m, w, l);
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        GetRawInputData(l, 0x10000003, IntPtr.Zero, ref size, headerSize);
        if (size >= headerSize)
        {
            var mem = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(l, 0x10000003, mem, ref size, headerSize) == (int)size &&
                    size >= headerSize + Marshal.SizeOf<RawMouse>() &&
                    Marshal.PtrToStructure<RawInputHeader>(mem).Type == RIM_TYPEMOUSE)
                {
                    var point = GetPoint();
                    RecordTargetMouse(Marshal.PtrToStructure<RawMouse>(IntPtr.Add(mem, (int)headerSize)), point);
                }
            }
            catch { Fail(31); }
            finally { Marshal.FreeHGlobal(mem); }
        }
        return DefWindowProcW(h, m, w, l);
    }
    private static void Loop()
    {
        _threadId = GetCurrentThreadId();
        var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr window = IntPtr.Zero;
        bool mouseRegistered = false;
        try
        {
            var wc = new WndClass { Name = "crer.raw.input", Proc = WindowProc, Instance = GetModuleHandleW(null) };
            RegisterClassW(ref wc);
            window = CreateWindowExW(0, wc.Name, wc.Name, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.Instance, IntPtr.Zero);
            if (window == IntPtr.Zero) { Fail(Marshal.GetLastWin32Error()); return; }
            if (_testHookFailure != 0)
            {
                var error = _testHookFailure;
                _testHookFailure = 0;
                Fail(error);
                return;
            }
            // INPUTSINK receives physical mouse input even while CfT owns the foreground.
            // Do not also install a mouse hook: that would record each click twice.
            mouseRegistered = RegisterRawInputDevices(
                new[] { new RawInputDevice { UsagePage=1, Usage=2, Flags=0x100, Target=window } },
                1, (uint)Marshal.SizeOf<RawInputDevice>());
            if (!mouseRegistered) { Fail(Marshal.GetLastWin32Error()); return; }
            _keyboardHook = SetWindowsHookExW(13, KeyboardHookProc, IntPtr.Zero, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                Fail(Marshal.GetLastWin32Error());
                return;
            }
            while (_running)
            {
                var messageStatus = GetMessageW(out var message, IntPtr.Zero, 0, 0);
                if (messageStatus > 0) { DispatchMessageW(ref message); continue; }
                if (messageStatus < 0) Fail(Marshal.GetLastWin32Error());
                break;
            }
        }
        catch
        {
            Fail(Marshal.GetLastWin32Error() is var error && error != 0 ? error : 31);
        }
        finally
        {
            if (mouseRegistered) RegisterRawInputDevices(
                new[] { new RawInputDevice { UsagePage=1, Usage=2, Flags=1 } },
                1, (uint)Marshal.SizeOf<RawInputDevice>());
            if (window != IntPtr.Zero) DestroyWindow(window);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            _mouseHook = _keyboardHook = IntPtr.Zero;
            _running = false;
            _threadId = 0;
            if (previousDpiContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpiContext);
            Stopped.Set();
        }
    }
    internal static nint GetForegroundWindowHandle()
    {
        lock (WindowGate) return GetForegroundWindow();
    }
    internal static bool IsProcessForeground(uint pid)
    {
        lock (WindowGate)
        {
            var window = GetForegroundWindow();
            return window != IntPtr.Zero && GetWindowThreadProcessId(window, out var current) != 0 && current == pid;
        }
    }
    internal static int RestoreForegroundWindow(nint h)
    {
        lock (WindowGate)
        {
            if (h == IntPtr.Zero) return 87; // ERROR_INVALID_PARAMETER
            return SetForegroundWindow(h) ? 0 : Marshal.GetLastWin32Error();
        }
    }
    internal static int ForegroundProcessWindow(uint pid)
    {
        lock (WindowGate)
        {
            if (pid == 0) return 87; // ERROR_INVALID_PARAMETER
            _foregroundPid = pid;
            _foregroundTarget = IntPtr.Zero;
            EnumWindows(FindForeground, IntPtr.Zero);
            if (_foregroundTarget == IntPtr.Zero) return 1168; // ERROR_NOT_FOUND
            return TryBringToForeground(_foregroundTarget);
        }
    }
    private static int TryBringToForeground(IntPtr target)
    {
        // 入力キューを接続すると対象の UI 停止が呼び出し元にも伝播するため、前景要求だけを行う。
        if (IsIconic(target)) ShowWindowAsync(target, 9); // SW_RESTORE
        if (SetForegroundWindow(target)) return 0;
        var error = Marshal.GetLastWin32Error();
        return error == 0 ? 5 : error;
    }
    internal static int SetProcessTopmost(uint pid, bool enabled)
    {
        var result = SetProcessTopmostDetailed(pid, enabled);
        lock (WindowGate) _lastForegroundStatus = 0;
        if (result.Status != 0 || !enabled) return result.Status;
        var foreground = TryBringToForeground(result.Window);
        lock (WindowGate) _lastForegroundStatus = foreground;
        return 0;
    }
    internal static int SetProcessTopmostOnly(uint pid, bool enabled)
    {
        return SetProcessTopmostDetailed(pid, enabled).Status;
    }
    internal static TopmostResult SetProcessTopmostDetailed(uint pid, bool enabled)
        => SetProcessTopmostDetailed(pid, enabled, CancellationToken.None, TimeSpan.FromSeconds(1));

    internal static TopmostResult SetProcessTopmostDetailed(uint pid, bool enabled, CancellationToken cancellation, TimeSpan confirmationTimeout)
    {
        cancellation.ThrowIfCancellationRequested();
        if (confirmationTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(confirmationTimeout));
        if (pid == 0) return new(87, "invalid_process", 0, null, 0, null, 0);
        nint window;
        lock (WindowGate)
        {
            _foregroundPid = pid;
            _foregroundTarget = IntPtr.Zero;
            EnumWindows(FindForeground, IntPtr.Zero);
            window = _foregroundTarget;
        }
        if (window == IntPtr.Zero) return new(1168, "window_not_found", 0, null, 0, null, 0);
        // HWND の探索だけを共通ロック内で行い、対象 UI の反映待ちを別 profile へ伝播させない。
        bool? permissionGranted = enabled ? AllowSetForegroundWindow(pid) : null;
        var permissionError = permissionGranted == false ? Marshal.GetLastWin32Error() : 0;
        return ApplyTopmost(window, enabled, cancellation, confirmationTimeout) with
        {
            ForegroundPermissionGranted = permissionGranted,
            ForegroundPermissionError = permissionError,
        };
    }
    internal static TopmostResult ApplyTopmost(nint window, bool enabled)
        => ApplyTopmost(window, enabled, CancellationToken.None, TimeSpan.FromSeconds(1));

    internal static TopmostResult ApplyTopmost(nint window, bool enabled, CancellationToken cancellation, TimeSpan confirmationTimeout)
    {
        cancellation.ThrowIfCancellationRequested();
        if (confirmationTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(confirmationTimeout));
        var watch = Stopwatch.StartNew();
        TimeSpan Remaining() => TimeSpan.FromTicks(Math.Max(0, (confirmationTimeout - watch.Elapsed).Ticks));
        var firstBudget = enabled && confirmationTimeout > TimeSpan.FromMilliseconds(250)
            ? TimeSpan.FromMilliseconds(250) : confirmationTimeout;
        var initial = ApplyTopmostRequest(window, enabled, cancellation, firstBudget);
        if (!enabled || initial.FailureStage is not ("style_mismatch" or "confirmation_timeout") || Remaining() <= TimeSpan.Zero) return initial;

        // API が成功しても最前面属性が反映されない状態だけ、一度解除してから設定し直す。
        // 初回・解除・再設定は同じ有限予算を共有し、取消し後は追加の要求を送らない。
        var reset = ApplyTopmostRequest(window, false, cancellation, Remaining());
        if (reset.Status != 0)
            return initial with
            {
                Status = reset.Status, FailureStage = "reset_topmost",
                RecoveryAttempted = true, RecoveryInitialStyle = initial.ExtendedStyle,
                RecoveryResetSucceeded = reset.SetWindowPosSucceeded, RecoveryResetError = reset.SetWindowPosError,
            };

        var reapplied = ApplyTopmostRequest(window, true, cancellation, Remaining());
        return reapplied with
        {
            RecoveryAttempted = true, RecoveryInitialStyle = initial.ExtendedStyle,
            RecoveryResetSucceeded = reset.SetWindowPosSucceeded, RecoveryResetError = reset.SetWindowPosError,
            RecoveryReapplySucceeded = reapplied.SetWindowPosSucceeded,
            RecoveryReapplyError = reapplied.SetWindowPosError,
        };
    }
    private static TopmostResult ApplyTopmostRequest(nint window, bool enabled, CancellationToken cancellation, TimeSpan confirmationTimeout)
    {
        cancellation.ThrowIfCancellationRequested();
        if (confirmationTimeout <= TimeSpan.Zero) return new(1460, "confirmation_timeout", window, null, 0, null, 0);
        var watch = Stopwatch.StartNew();
        var insertAfter = enabled ? new IntPtr(-1) : new IntPtr(-2); // HWND_TOPMOST / HWND_NOTOPMOST
        // 別 UI スレッドへは要求を非同期送信し、呼び出し側を無応答ウィンドウへ接続しない。
        var succeeded = SetWindowPos(window, insertAfter, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x4000);
        var setError = Marshal.GetLastWin32Error();
        if (!succeeded)
            return new(setError == 0 ? 5 : setError, "set_window_pos", window, false, setError, null, 0);

        var sameThread = GetWindowThreadProcessId(window, out _) == GetCurrentThreadId();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var style = GetWindowLongPtrW(window, -20).ToInt64(); // GWL_EXSTYLE
            var readError = Marshal.GetLastWin32Error();
            if (style == 0 && readError != 0)
                return new(readError, "read_style", window, true, setError, null, readError);
            if (((style & 8) != 0) == enabled)
                return watch.Elapsed <= confirmationTimeout ? new(0, "none", window, true, setError, style, 0)
                    : new(1460, "confirmation_timeout", window, true, setError, style, 0);
            if (sameThread) return new(5, "style_mismatch", window, true, setError, style, 0);
            var remaining = confirmationTimeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) return new(1460, "confirmation_timeout", window, true, setError, style, 0);
            // 属性の反映前に成功・不一致を確定せず、短い待機にも取消しを適用する。
            if (cancellation.WaitHandle.WaitOne(remaining < TimeSpan.FromMilliseconds(5) ? remaining : TimeSpan.FromMilliseconds(5)))
                cancellation.ThrowIfCancellationRequested();
        }
    }
    internal static int LastForegroundStatus { get { lock (WindowGate) return _lastForegroundStatus; } }
    internal static int SetProcessOpacity(uint pid, uint alpha)
    {
        lock (WindowGate) return SetProcessOpacityCore(pid, alpha);
    }
    private static int SetProcessOpacityCore(uint pid, uint alpha)
    {
        if (pid == 0 || alpha > 255) return 87; // ERROR_INVALID_PARAMETER
        _foregroundPid = pid;
        _foregroundTarget = IntPtr.Zero;
        EnumWindows(FindForeground, IntPtr.Zero);
        if (_foregroundTarget == IntPtr.Zero) return 1168;
        var target = _foregroundTarget;
        var style = GetWindowLongPtrW(target, -20).ToInt64(); // GWL_EXSTYLE
        const long layered = 0x00080000; // WS_EX_LAYERED
        if ((style & layered) == 0)
        {
            if (alpha == 255) return 0;
            // .NET clears native last-error before a SetLastError=true P/Invoke.
            var previous = SetWindowLongPtrW(target, -20, new IntPtr(style | layered));
            var error = Marshal.GetLastWin32Error();
            if (previous == IntPtr.Zero && error != 0) return error;
        }
        else if (GetLayeredWindowAttributes(target, out _, out var currentAlpha, out var flags)
            && flags == 2 && currentAlpha == alpha) return 0;
        // Only composition changes: never change Z order, focus, or hit-test flags.
        if (SetLayeredWindowAttributes(target, 0, (byte)alpha, 2)) return 0; // LWA_ALPHA
        var status = Marshal.GetLastWin32Error();
        return status == 0 ? 5 : status;
    }
    internal static ulong QpcFrequency(){ QueryPerformanceFrequency(out var frequency); return (ulong)frequency; }
    private static int StartCore(uint pid)
    {
        if (_running || (_thread?.IsAlive ?? false)) return 183;
        while (Queue.TryDequeue(out _)) { }
        Stopped.Reset();
        _pid = pid;
        _error = 0;
        _threadId = 0;
        lock (WindowGate)
        {
            _target = _content = IntPtr.Zero;
            _contentRelativeRect = null;
            _ = TryContentRect(out _);
        }
        _mouseHook = _keyboardHook = IntPtr.Zero;
        InitializeKeyboardState(GetKeyState(0x14), GetAsyncKeyState(0x14));
        _leftButtonAccepted = false;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
        return 0;
    }
    private static int StopCore()
    {
        if (_testStopTimeout != 0)
        {
            _testStopTimeout = 0;
            return 1460; // Injected ERROR_TIMEOUT for the recorder lifecycle test.
        }
        var thread = _thread;
        if (thread is null || !thread.IsAlive) return _error;
        _running = false;
        if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
        if (!Stopped.WaitOne(5000)) return 1460; // ERROR_TIMEOUT
        return _error;
    }
    internal static int Start(uint pid)=>StartCore(pid);
    internal static bool IsRunning=>_running;
    internal static int Stop()=>StopCore();
    internal static int Read(Span<InputEvent> output)
    {
        var count = 0;
        while (count < output.Length && Queue.TryDequeue(out var input)) output[count++] = input;
        return count;
    }
    internal static ContentBounds GetContentRect()
    {
        lock (WindowGate)
        {
            if (!TryContentRect(out var rect)) return new(_target == IntPtr.Zero ? 1168 : 1169, default);
            return new(0, new ContentRect { X = rect.Left, Y = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top });
        }
    }
    internal static int LastError=>_error;

    internal static void TestPush()=>Push(1);
    internal static void TestMousePacket(byte[] packet, int x, int y)
    {
        var memory = Marshal.AllocHGlobal(packet.Length);
        try {
            Marshal.Copy(packet, 0, memory, packet.Length);
            RecordMouse(Marshal.PtrToStructure<RawMouse>(memory), new Point { X=x, Y=y });
        } finally { Marshal.FreeHGlobal(memory); }
    }
    internal static void TestFailHookInitialization(int error)=>_testHookFailure=error;
    internal static void TestForceStopTimeout()=>_testStopTimeout=1;
    internal static void TestMouseAt(ushort flags, int x, int y, int moveX = 0) => RecordTargetMouse(new RawMouse { ButtonFlags = flags, LastX = moveX }, new Point { X = x, Y = y });
    internal static void TestKeyboardAt(uint key, uint scan, uint message, nint foreground) => RecordKeyboard(new LowLevelKeyboard { VKey = key, ScanCode = scan }, message, foreground);
    internal static uint TestCharacter(uint key, uint scan) => TryCharacter(new LowLevelKeyboard { VKey = key, ScanCode = scan }, out var character) ? character : 0;
    internal static void TestInitialKeyboardState(bool capsLock, bool capsPressed) => InitializeKeyboardState((short)(capsLock ? 1 : 0), (short)(capsPressed ? short.MinValue : 0));
}
