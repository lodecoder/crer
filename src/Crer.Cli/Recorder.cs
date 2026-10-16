using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Crer.Windows;

namespace Crer;

public sealed record RecordRawOptions
{
    public NormalizePoint? Viewport { get; init; }
    public Func<CancellationToken, Task<NormalizePoint?>>? MarkerClick { get; init; }
    public Func<CancellationToken, Task<NormalizePoint>>? ReadViewport { get; init; }
    public Func<bool>? IsTargetAlive { get; init; }
    public NormalizeWindowBounds? WindowBounds { get; init; }
    public NormalizePoint? RequestedContent { get; init; }
    public string? ProfileDir { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(16);
    public TextWriter Output { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;
}

public static partial class Recorder
{
    private static readonly JsonSerializerOptions MetadataFormat = new() { WriteIndented = true };

    public static async Task RecordRaw(IInputRecorder native, uint pid, string output, RecordRawOptions options, CancellationToken token = default)
    {
        var started = false;
        Exception? primary = null;
        var stopStatus = 0;
        try
        {
            var frequency = native.QpcFrequencyHz.ToString(CultureInfo.InvariantCulture);
            var start = native.Start(pid);
            if (start != 0) throw new CrerException(3, $"Raw Input start failed: {start}");
            started = true;
            options.Error.WriteLine($"Recording target Chrome process: {pid}");
            JsonObject? calibration = null;
            var activeViewport = options.Viewport;
            var nextViewportCheck = 0L;
            NormalizePoint? mouseDown = null;
            var mouseMoved = false;
            void LogClick(NormalizePoint point)
            {
                var bounds = native.GetContentRect();
                if (calibration is not null && activeViewport is not null && bounds.Status == 0 && bounds.Rect.Width >= 32 && bounds.Rect.Height >= 32)
                {
                    var screen = calibration["screenClick"]!;
                    var css = calibration["cssPoint"]!;
                    var x = Round(point.X - (Json.Number(screen["x"]) - Json.Number(css["x"])));
                    var y = Round(point.Y - (Json.Number(screen["y"]) - Json.Number(css["y"])));
                    options.Output.WriteLine($"[crer] click: {{ x: {x.ToString(CultureInfo.InvariantCulture)}, y: {y.ToString(CultureInfo.InvariantCulture)} }}");
                }
                else options.Output.WriteLine($"[crer] click screen_px: {{ x: {point.X.ToString(CultureInfo.InvariantCulture)}, y: {point.Y.ToString(CultureInfo.InvariantCulture)} }}");
            }
            async Task<ContentBounds> WriteMetadata(Exception? recordingError)
            {
                var bounds = native.GetContentRect();
                var metadata = new JsonObject { ["qpc_frequency_hz"] = frequency };
                if (bounds.Status == 0 && bounds.Rect.Width >= 32 && bounds.Rect.Height >= 32)
                    metadata["content_rect_screen_px"] = new JsonObject
                    {
                        ["x"] = bounds.Rect.X, ["y"] = bounds.Rect.Y, ["width"] = bounds.Rect.Width, ["height"] = bounds.Rect.Height,
                    };
                if (options.Viewport is { } initialViewport)
                {
                    metadata["css_viewport"] = Point(initialViewport);
                    metadata["screen_pixels_per_css_pixel"] = new JsonObject { ["x"] = 1, ["y"] = 1 };
                }
                if (options.RequestedContent is { } requested) metadata["requested_content"] = new JsonObject { ["width"] = requested.X, ["height"] = requested.Y };
                if (options.ProfileDir is { Length: > 0 } profile) metadata["profile_dir"] = profile;
                if (options.WindowBounds is { } window) metadata["window_bounds"] = new JsonObject { ["left"] = window.Left, ["top"] = window.Top };
                if (calibration is not null) metadata["marker_calibration"] = calibration.DeepClone();
                if (recordingError is not null)
                {
                    metadata["incomplete"] = true;
                    metadata["recording_error"] = recordingError.Message;
                }
                await File.WriteAllTextAsync(output + ".meta.json", metadata.ToJsonString(MetadataFormat) + "\n", new UTF8Encoding(false), CancellationToken.None);
                return bounds;
            }

            options.Error.WriteLine("Click the 64x64 magenta marker at the page's upper-left corner to calibrate and begin recording.");
            Exception? recordingError = null, metadataError = null;
            var finalBounds = new ContentBounds(0, default);
            // The established format appends raw input, including when a caller deliberately resumes a file.
            await using var stream = new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
            try
            {
                var events = new InputEvent[256];
                while (!token.IsCancellationRequested)
                {
                    if (options.IsTargetAlive?.Invoke() == false) break;
                    if (!native.IsRunning)
                    {
                        var status = native.LastError;
                        throw new CrerException(3, status == 0 ? "Raw Input recorder stopped unexpectedly" : $"Raw Input recorder failed: {status}");
                    }
                    if (options.ReadViewport is not null && Environment.TickCount64 >= nextViewportCheck)
                    {
                        NormalizePoint next;
                        try { next = await options.ReadViewport(token); }
                        catch (CrerException error) when (error.Code == 3 && options.IsTargetAlive?.Invoke() == false)
                        {
                            // The user may close CfT after the loop's liveness check but before
                            // this CDP read completes. A confirmed process exit is a normal stop.
                            break;
                        }
                        if (activeViewport is not null && next != activeViewport)
                            options.Error.WriteLine($"Note: recording viewport changed from {activeViewport.X}x{activeViewport.Y} to {next.X}x{next.Y}; subsequent input will use the new coordinate space.");
                        activeViewport = next;
                        nextViewportCheck = Environment.TickCount64 + 250;
                    }
                    var count = native.Read(events);
                    for (var index = 0; index < count; index++)
                    {
                        var input = events[index];
                        var item = new JsonObject
                        {
                            ["qpc"] = input.Qpc.ToString(CultureInfo.InvariantCulture), ["x"] = input.X, ["y"] = input.Y,
                            ["kind"] = input.Kind, ["data"] = input.Data,
                        };
                        if (activeViewport is not null && options.Viewport is not null && activeViewport != options.Viewport)
                            item["css_viewport"] = Point(activeViewport);
                        if (calibration is null && options.MarkerClick is not null && options.Viewport is not null)
                        {
                            try
                            {
                                if (input.Kind == 3)
                                {
                                    await Task.Delay(20, token);
                                    if (await options.MarkerClick(token) is { } css)
                                    {
                                        calibration = new JsonObject { ["screenClick"] = new JsonObject { ["x"] = input.X, ["y"] = input.Y }, ["cssPoint"] = Point(css) };
                                        options.Error.WriteLine("Calibration complete. Recording browser interactions now.");
                                    }
                                }
                            }
                            catch (Exception error) when (error is CrerException or OperationCanceledException or HttpRequestException)
                            { /* Explicit coordinate normalization remains available if CDP cannot read the marker. */ }
                            continue;
                        }
                        if (input.Kind == 2) { mouseDown = new(input.X, input.Y); mouseMoved = false; }
                        else if (input.Kind == 1 && mouseDown is not null && (input.X != mouseDown.X || input.Y != mouseDown.Y)) mouseMoved = true;
                        else if (input.Kind == 3)
                        {
                            if (mouseDown is not null && !mouseMoved) LogClick(new(input.X, input.Y));
                            mouseDown = null;
                            mouseMoved = false;
                        }
                        await writer.WriteLineAsync(item.ToJsonString());
                    }
                    try { await Task.Delay(options.PollInterval, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { recordingError = error; throw; }
            finally
            {
                await writer.FlushAsync(CancellationToken.None);
                try { finalBounds = await WriteMetadata(recordingError); }
                catch (Exception error)
                {
                    metadataError = error;
                    if (recordingError is not null) options.Error.WriteLine($"Failed to write incomplete recording metadata: {error.Message}");
                }
            }
            if (metadataError is not null) ExceptionDispatchInfo.Capture(metadataError).Throw();
            if (options.MarkerClick is not null && calibration is null)
                throw new CrerException(3, "recording calibration marker was not clicked");
            if (finalBounds.Status != 0 || finalBounds.Rect.Width < 32 || finalBounds.Rect.Height < 32)
                options.Error.WriteLine($"Warning: CfT content bounds were unavailable (Win32 status {finalBounds.Status}); normalize may require explicit coordinate options.");
        }
        catch (Exception error) { primary = error; }
        finally { if (started) stopStatus = native.Stop(); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (stopStatus != 0)
        {
            var error = $"Raw Input stop failed: {stopStatus}";
            var metadata = Normalize.ReadSidecar(output) ?? new JsonObject();
            metadata["incomplete"] = true;
            metadata["recording_error"] = error;
            await File.WriteAllTextAsync(output + ".meta.json", metadata.ToJsonString(MetadataFormat) + "\n", new UTF8Encoding(false));
            throw new CrerException(3, error);
        }
    }

    private static JsonObject Point(NormalizePoint point) => new() { ["x"] = point.X, ["y"] = point.Y };
    private static double Round(double value) { var floor = Math.Floor(value); return value - floor >= 0.5 ? floor + 1 : floor; }
}
