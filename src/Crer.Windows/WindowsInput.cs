using System;

namespace Crer.Windows;

/// <summary>Windows API を使い、物理入力の記録とブラウザーのウィンドウ操作を行う。</summary>
public sealed class WindowsInput : IInputRecorder, IDisposable
{
    // プロセス内では Raw Input の登録と記録用メッセージループを一つだけ共有する。
    private static readonly object RecorderGate = new();
    private static WindowsInput? currentOwner;
    private bool disposed;

    public WindowsInput()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows input requires Windows.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public ulong QpcFrequencyHz { get { ThrowIfDisposed(); return InputBridge.QpcFrequency(); } }
    public int LastError { get { ThrowIfDisposed(); return InputBridge.LastError; } }
    public bool IsRunning { get { ThrowIfDisposed(); return InputBridge.IsRunning; } }

    public int Start(uint pid)
    {
        lock (RecorderGate)
        {
            ThrowIfDisposed();
            var status = InputBridge.Start(pid);
            if (status == 0) currentOwner = this;
            return status;
        }
    }

    public int Stop()
    {
        lock (RecorderGate)
        {
            ThrowIfDisposed();
            if (currentOwner != this) return 0;
            var status = InputBridge.Stop();
            if (status != 1460) currentOwner = null;
            return status;
        }
    }

    public int Read(Span<InputEvent> events)
    {
        ThrowIfDisposed();
        return InputBridge.Read(events);
    }

    public ContentBounds GetContentRect()
    {
        ThrowIfDisposed();
        return InputBridge.GetContentRect();
    }

    public nint GetForegroundWindow() { ThrowIfDisposed(); return InputBridge.GetForegroundWindowHandle(); }
    public bool IsProcessForeground(uint processId) { ThrowIfDisposed(); return InputBridge.IsProcessForeground(processId); }
    public int RestoreForegroundWindow(nint window) { ThrowIfDisposed(); return InputBridge.RestoreForegroundWindow(window); }
    public int ForegroundProcessWindow(uint pid) { ThrowIfDisposed(); return InputBridge.ForegroundProcessWindow(pid); }
    public int SetProcessTopmost(uint pid, bool enabled) { ThrowIfDisposed(); return InputBridge.SetProcessTopmost(pid, enabled); }
    public int SetProcessTopmostOnly(uint pid, bool enabled) { ThrowIfDisposed(); return InputBridge.SetProcessTopmostOnly(pid, enabled); }
    public int LastForegroundStatus { get { ThrowIfDisposed(); return InputBridge.LastForegroundStatus; } }
    public int SetProcessOpacity(uint pid, uint alpha) { ThrowIfDisposed(); return InputBridge.SetProcessOpacity(pid, alpha); }

    public void Dispose()
    {
        lock (RecorderGate)
        {
            if (disposed) return;
            // 停止がタイムアウトした場合は、所有者を有効に保って再試行できるようにする。
            if (currentOwner == this && Stop() == 1460) return;
            disposed = true;
        }
    }
}
