using System;

namespace Crer.Windows;

/// <summary>QueryPerformanceCounter の時刻と物理画面ピクセル座標を持つ入力サンプル。</summary>
public struct InputEvent
{
    public ulong Qpc;
    public int X, Y;
    public uint Kind, Data;
}

public struct ContentRect
{
    public int X, Y, Width, Height;
}

public readonly record struct ContentBounds(int Status, ContentRect Rect);

public interface IInputRecorder
{
    ulong QpcFrequencyHz { get; }
    int Start(uint pid);
    bool IsRunning { get; }
    int Stop();
    int LastError { get; }
    int Read(Span<InputEvent> events);
    ContentBounds GetContentRect();
}
