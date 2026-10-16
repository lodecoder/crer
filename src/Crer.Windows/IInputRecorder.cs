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
    /// <summary>入力生成を停止して未読イベントを保持する。停止後も Read と GetContentRect を呼び出せる。</summary>
    int Stop();
    int LastError { get; }
    /// <summary>停止後も未読イベントを読み出せる。キューが空なら 0 を返し、次の Start は未読イベントを破棄する。</summary>
    int Read(Span<InputEvent> events);
    ContentBounds GetContentRect();
}
