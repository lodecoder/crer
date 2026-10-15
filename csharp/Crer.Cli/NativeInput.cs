using System.Runtime.InteropServices;

namespace Crer;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeInputEvent
{
    public ulong Qpc;
    public int X, Y;
    public uint Kind, Data;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeContentRect
{
    public int X, Y, Width, Height;
}

public readonly record struct NativeContentBounds(int Status, NativeContentRect Rect);

public interface INativeRecorder
{
    uint AbiVersion { get; }
    ulong QpcFrequencyHz { get; }
    int Start(uint pid);
    bool IsRunning { get; }
    int Stop();
    int LastError { get; }
    int Read(Span<NativeInputEvent> events);
    NativeContentBounds GetContentRect();
}

/// <summary>Calls the native AOT DLL ABI; never invokes UnmanagedCallersOnly as managed code.</summary>
public sealed unsafe class NativeInput : INativeRecorder, IDisposable
{
    // The native ABI resolves HWNDs through shared variables. Serializing those exports also
    // protects parallel scenarios which load separate handles for the same DLL.
    private static readonly object WindowGate = new();
    private nint handle;
    private bool started;

    public NativeInput(string dllPath)
    {
        if (!OperatingSystem.IsWindows()) throw new CrerException(3, "native input requires Windows");
        try { handle = NativeLibrary.Load(Path.GetFullPath(dllPath)); }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or IOException or ArgumentException)
        { throw new CrerException(3, $"Could not load crer-win-input.dll: {error.Message}"); }
    }

    private nint Export(string name)
    {
        if (handle == 0) throw new ObjectDisposedException(nameof(NativeInput));
        if (!NativeLibrary.TryGetExport(handle, name, out var function))
            throw new CrerException(3, $"Could not load {name}; rebuild crer-win-input.dll");
        return function;
    }

    public uint AbiVersion => ((delegate* unmanaged<uint>)Export("crer_input_abi_version"))();
    public ulong QpcFrequencyHz => ((delegate* unmanaged<ulong>)Export("crer_input_qpc_frequency"))();
    public int LastError => ((delegate* unmanaged<int>)Export("crer_input_last_error"))();
    public bool IsRunning => ((delegate* unmanaged<int>)Export("crer_input_is_running"))() != 0;

    public int Start(uint pid)
    {
        var status = ((delegate* unmanaged<uint, int>)Export("crer_input_start"))(pid);
        if (status == 0) started = true;
        return status;
    }

    public int Stop()
    {
        var status = ((delegate* unmanaged<int>)Export("crer_input_stop"))();
        // ERROR_TIMEOUT means hook callbacks may still execute; keep the DLL loaded.
        if (status != 1460) started = false;
        return status;
    }

    public int Read(Span<NativeInputEvent> events)
    {
        var read = (delegate* unmanaged<NativeInputEvent*, uint, uint>)Export("crer_input_read");
        fixed (NativeInputEvent* pointer = events)
        {
            var count = read(pointer, (uint)events.Length);
            if (count > events.Length) throw new CrerException(3, "Raw Input DLL returned an invalid event count");
            return (int)count;
        }
    }

    public NativeContentBounds GetContentRect()
    {
        var rect = new NativeContentRect();
        var status = ((delegate* unmanaged<NativeContentRect*, int>)Export("crer_input_get_content_rect"))(&rect);
        return new NativeContentBounds(status, rect);
    }

    public nint GetForegroundWindow()
    {
        lock (WindowGate) return ((delegate* unmanaged<nint>)Export("crer_input_get_foreground_window"))();
    }
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    public bool IsProcessForeground(uint processId)
    {
        lock (WindowGate)
        {
            var window = GetForegroundWindow();
            return window != 0 && GetWindowThreadProcessId(window, out var current) != 0 && current == processId;
        }
    }
    public int RestoreForegroundWindow(nint window)
    {
        lock (WindowGate) return ((delegate* unmanaged<nint, int>)Export("crer_input_restore_foreground_window"))(window);
    }
    public int ForegroundProcessWindow(uint pid)
    {
        lock (WindowGate) return ((delegate* unmanaged<uint, int>)Export("crer_input_foreground_process_window"))(pid);
    }
    public int SetProcessTopmost(uint pid, bool enabled)
    {
        lock (WindowGate) return ((delegate* unmanaged<uint, int, int>)Export("crer_input_set_process_topmost"))(pid, enabled ? 1 : 0);
    }
    public int SetProcessTopmostOnly(uint pid, bool enabled)
    {
        lock (WindowGate) return ((delegate* unmanaged<uint, int, int>)Export("crer_input_set_process_topmost_only"))(pid, enabled ? 1 : 0);
    }
    public int LastForegroundStatus
    {
        get { lock (WindowGate) return ((delegate* unmanaged<int>)Export("crer_input_last_foreground_status"))(); }
    }
    public int SetProcessOpacity(uint pid, uint alpha)
    {
        lock (WindowGate) return ((delegate* unmanaged<uint, uint, int>)Export("crer_input_set_process_opacity"))(pid, alpha);
    }

    public void Dispose()
    {
        if (handle == 0) return;
        if (started && Stop() == 1460) return;
        NativeLibrary.Free(handle);
        handle = 0;
    }
}
