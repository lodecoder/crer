using System;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static class InputBridge
{
    private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RI_MOUSE_LEFT_BUTTON_DOWN = 1, RI_MOUSE_LEFT_BUTTON_UP = 2, RI_MOUSE_WHEEL = 0x400;
    private static readonly ConcurrentQueue<CrerInputEvent> Queue = new();
    private static readonly AutoResetEvent Stopped = new(false);
    private static Thread? _thread; private static volatile bool _running; private static uint _pid; private static int _error; private static IntPtr _target, _mouseHook, _keyboardHook; private static uint _threadId;
    [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct CrerInputEvent { public ulong Qpc; public int X, Y; public uint Kind, Data; }
    [StructLayout(LayoutKind.Sequential)] internal struct CrerRect { public int X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader { public uint Type, Size; public IntPtr Device; public IntPtr WParam; }
    [StructLayout(LayoutKind.Explicit)] private struct RawInput { [FieldOffset(0)] public RawInputHeader Header; [FieldOffset(24)] public RawMouse Mouse; [FieldOffset(24)] public RawKeyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct RawMouse { public ushort Flags, ButtonFlags, ButtonData; public uint RawButtons; public int LastX, LastY, Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct RawKeyboard { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr Hwnd; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Pt; }
    [StructLayout(LayoutKind.Sequential)] private struct LowLevelMouse { public Point Point; public uint MouseData, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct LowLevelKeyboard { public uint VKey, ScanCode, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError=true)] private static extern bool RegisterRawInputDevices(RawInputDevice[] d, uint n, uint cb);
    [DllImport("user32.dll")] private static extern int GetRawInputData(IntPtr h, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, char[] name, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Point point);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern ushort RegisterClassW(ref WndClass c);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint e,string c,string n,uint s,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc p, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, UIntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, UIntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h,uint m,UIntPtr w,IntPtr l);
    private delegate IntPtr WndProc(IntPtr h,uint m,UIntPtr w,IntPtr l);
    private delegate IntPtr HookProc(int code, UIntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct WndClass { public uint Style; public WndProc Proc; public int ClsExtra, WndExtra; public IntPtr Instance, Icon, Cursor, Background; public string Name; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? n);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long n);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static readonly HookProc MouseHookProc = MouseHook;
    private static readonly HookProc KeyboardHookProc = KeyboardHook;
    private static bool Find(IntPtr h, IntPtr _)
    {
        GetWindowThreadProcessId(h, out var p);
        if (p != _pid) return true;
        var name = new char[256];
        if (GetClassNameW(h, name, name.Length) == 0 ||
            !new string(name).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) return true;
        _target = h;
        return false;
    }
    private static bool FindContent(IntPtr h, IntPtr output)
    {
        var name = new char[256];
        if (GetClassNameW(h, name, name.Length) > 0 && new string(name).StartsWith("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal))
        {
            if (!GetClientRect(h, out var rect) || rect.Right - rect.Left < 32 || rect.Bottom - rect.Top < 32) return true;
            Marshal.WriteIntPtr(output, h);
            return false;
        }
        return true;
    }
    private static bool Active(bool mouse)
    {
        IntPtr h = mouse ? WindowFromPoint(GetPoint()) : GetForegroundWindow();
        var root = GetAncestor(h, 2);
        if (root == IntPtr.Zero) return false;
        GetWindowThreadProcessId(root, out var p);
        if (p != _pid) return false;
        _target = root;
        return true;
    }
    private static Point GetPoint(){ GetCursorPos(out var p); return p; }
    private static void Push(uint kind,uint data=0){ if(Queue.Count>=8192){_error=111;_running=false;return;} QueryPerformanceCounter(out var q); var p=GetPoint(); Queue.Enqueue(new(){Qpc=(ulong)q,X=p.X,Y=p.Y,Kind=kind,Data=data}); }
    private static IntPtr MouseHook(int code, UIntPtr w, IntPtr l)
    {
        if (code >= 0 && Active(true)) {
            var input = Marshal.PtrToStructure<LowLevelMouse>(l);
            switch ((uint)w) {
                case 0x0200: Push(1); break;
                case 0x0201: Push(2); break;
                case 0x0202: Push(3); break;
                case 0x020A: Push(6, input.MouseData); break;
            }
        }
        return CallNextHookEx(_mouseHook, code, w, l);
    }
    private static IntPtr KeyboardHook(int code, UIntPtr w, IntPtr l)
    {
        if (code >= 0 && Active(false)) {
            var input = Marshal.PtrToStructure<LowLevelKeyboard>(l);
            var kind = (uint)w is 0x0101 or 0x0105 ? 8u : 7u;
            if ((uint)w is 0x0100 or 0x0101 or 0x0104 or 0x0105) Push(kind, (input.VKey << 16) | input.ScanCode);
        }
        return CallNextHookEx(_keyboardHook, code, w, l);
    }
    private static IntPtr Proc(IntPtr h,uint m,UIntPtr w,IntPtr l){ if(m!=0x00FF) return DefWindowProcW(h,m,w,l); uint size=0; GetRawInputData(l,0x10000003,IntPtr.Zero,ref size,(uint)Marshal.SizeOf<RawInputHeader>()); var mem=Marshal.AllocHGlobal((int)size); try { if(GetRawInputData(l,0x10000003,mem,ref size,(uint)Marshal.SizeOf<RawInputHeader>())!=(int)size)return IntPtr.Zero; var r=Marshal.PtrToStructure<RawInput>(mem); if(r.Header.Type==RIM_TYPEMOUSE&&Active(true)){var x=r.Mouse;if(x.LastX!=0||x.LastY!=0)Push(1);if((x.ButtonFlags&RI_MOUSE_LEFT_BUTTON_DOWN)!=0)Push(2);if((x.ButtonFlags&RI_MOUSE_LEFT_BUTTON_UP)!=0)Push(3);if((x.ButtonFlags&RI_MOUSE_WHEEL)!=0)Push(6,x.ButtonData);}else if(r.Header.Type==RIM_TYPEKEYBOARD&&Active(false))Push((r.Keyboard.Flags&1)!=0?8u:7u,((uint)r.Keyboard.VKey<<16)|r.Keyboard.MakeCode); } finally{Marshal.FreeHGlobal(mem);} return IntPtr.Zero; }
    private static void Loop(){ _threadId=GetCurrentThreadId(); var wc=new WndClass{Name="crer.raw.input",Proc=Proc,Instance=GetModuleHandleW(null)};RegisterClassW(ref wc);var h=CreateWindowExW(0,wc.Name,wc.Name,0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,wc.Instance,IntPtr.Zero);_mouseHook=SetWindowsHookExW(14,MouseHookProc,IntPtr.Zero,0);_keyboardHook=SetWindowsHookExW(13,KeyboardHookProc,IntPtr.Zero,0);if(_mouseHook==IntPtr.Zero||_keyboardHook==IntPtr.Zero){_error=Marshal.GetLastWin32Error();_running=false;}while(_running&&GetMessageW(out _,IntPtr.Zero,0,0)>0){}if(_mouseHook!=IntPtr.Zero)UnhookWindowsHookEx(_mouseHook);if(_keyboardHook!=IntPtr.Zero)UnhookWindowsHookEx(_keyboardHook);_mouseHook=_keyboardHook=IntPtr.Zero;Stopped.Set(); }
    [UnmanagedCallersOnly(EntryPoint="crer_input_abi_version")] public static uint Version()=>1;
    [UnmanagedCallersOnly(EntryPoint="crer_input_start")] public static int Start(uint pid){if(_running)return 183;_pid=pid;_error=0;_running=true;_thread=new Thread(Loop){IsBackground=true};_thread.Start();return 0;}
    [UnmanagedCallersOnly(EntryPoint="crer_input_stop")] public static int Stop(){_running=false;if(_threadId!=0)PostThreadMessageW(_threadId,0x0012,UIntPtr.Zero,IntPtr.Zero);Stopped.WaitOne(1000);return _error;}
    [UnmanagedCallersOnly(EntryPoint="crer_input_read")] public static unsafe uint Read(CrerInputEvent* output,uint capacity){uint n=0;while(n<capacity&&Queue.TryDequeue(out var e))output[n++]=e;return n;}
    [UnmanagedCallersOnly(EntryPoint="crer_input_get_content_rect")] public static unsafe int GetContentRect(CrerRect* output)
    {
        if (output == null) return 87;
        if (_target == IntPtr.Zero) EnumWindows(Find, IntPtr.Zero);
        if (_target == IntPtr.Zero) return 1168; // ERROR_NOT_FOUND: browser top-level window not found.
        var handle = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(handle, IntPtr.Zero);
            EnumChildWindows(_target, FindContent, handle);
            var content = Marshal.ReadIntPtr(handle);
            if (content == IntPtr.Zero || !GetClientRect(content, out var rect)) return 1169; // Content HWND not found.
            var point = new Point { X = rect.Left, Y = rect.Top };
            if (!ClientToScreen(content, ref point)) return Marshal.GetLastWin32Error();
            *output = new CrerRect { X = point.X, Y = point.Y, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top };
            return 0;
        }
        finally { Marshal.FreeHGlobal(handle); }
    }
    [UnmanagedCallersOnly(EntryPoint="crer_input_last_error")] public static int Error()=>_error;
}
