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
    int StyleReadError)
{
    public bool? ForegroundPermissionGranted { get; init; }
    public int ForegroundPermissionError { get; init; }
    public bool RecoveryAttempted { get; init; }
    public long? RecoveryInitialStyle { get; init; }
    public bool? RecoveryResetSucceeded { get; init; }
    public int RecoveryResetError { get; init; }
    public bool? RecoveryReapplySucceeded { get; init; }
    public int RecoveryReapplyError { get; init; }
}

public interface IWindowInput : IDisposable
{
    nint GetForegroundWindow();
    bool IsProcessForeground(uint processId);
    int RestoreForegroundWindow(nint window);
    int ForegroundProcessWindow(uint pid);
    TopmostResult SetProcessTopmostDetailed(uint pid, bool enabled);
    TopmostResult SetProcessTopmostDetailed(uint pid, bool enabled, CancellationToken cancellation, TimeSpan confirmationTimeout)
    {
        cancellation.ThrowIfCancellationRequested();
        return SetProcessTopmostDetailed(pid, enabled);
    }
    int SetProcessOpacity(uint pid, uint alpha);
}
