using System;

namespace Crer.Windows;

/// <summary>最前面属性の設定結果。OS の実エラーと設定後の属性不一致を区別する。</summary>
public sealed record TopmostResult(
    int Status,
    string FailureStage,
    nint Window,
    bool? SetWindowPosSucceeded,
    int SetWindowPosError,
    long? ExtendedStyle,
    int StyleReadError);

public interface IWindowInput : IDisposable
{
    nint GetForegroundWindow();
    bool IsProcessForeground(uint processId);
    int RestoreForegroundWindow(nint window);
    int ForegroundProcessWindow(uint pid);
    TopmostResult SetProcessTopmostDetailed(uint pid, bool enabled);
    int SetProcessOpacity(uint pid, uint alpha);
}
