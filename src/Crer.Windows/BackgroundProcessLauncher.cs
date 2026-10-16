using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Crer.Windows;

/// <summary>An owned process and its redirected pipes. Dispose after draining the readers.</summary>
public sealed class BackgroundProcess : IDisposable
{
    private int disposed;
    public Process Process { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }

    internal BackgroundProcess(Process process, StreamReader output, StreamReader error)
        => (Process, StandardOutput, StandardError) = (process, output, error);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Exception? failure = null;
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                if (failure == null) failure = error;
                else BackgroundProcessLauncher.AttachCleanup(failure, error);
            }
        }
        Cleanup(() =>
        {
            if (Process.HasExited) return;
            Process.Kill(entireProcessTree: true);
            if (!Process.WaitForExit(5000)) throw new IOException("Owned background process did not exit after termination");
        });
        Cleanup(StandardOutput.Dispose);
        Cleanup(StandardError.Dispose);
        Cleanup(Process.Dispose);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

/// <summary>
/// Starts an explicit Windows executable with SW_SHOWNOACTIVATE. Only the three
/// stdio handles are inherited (STARTUPINFOEX HANDLE_LIST); stdin is already EOF.
/// No window is subsequently moved, focused, hidden, or restored by this class.
/// </summary>
public static unsafe class BackgroundProcessLauncher
{
    // Callers which also use Process.Start in this process must take this gate
    // around those starts: the BCL can inherit other temporarily inheritable
    // handles even though this launcher's own children use an explicit list.
    public static object CreationGate { get; } = new();
    private const uint HandleFlagInherit = 1, CreateSuspended = 4, CreateUnicodeEnvironment = 0x400,
        ExtendedStartupInfoPresent = 0x80000, CreateNoWindow = 0x08000000;
    private const uint StartfUseShowWindow = 1, StartfForceOffFeedback = 0x80, StartfUseStdHandles = 0x100;
    private const uint WaitObject0 = 0, WaitTimeout = 258, WaitFailed = uint.MaxValue;
    private const nuint ProcThreadAttributeHandleList = 0x20002;

    public static BackgroundProcess Start(ProcessStartInfo start)
    {
        lock (CreationGate) return StartExclusive(start);
    }

    private static BackgroundProcess StartExclusive(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Background process startup requires Windows");
        if (start.UseShellExecute || !start.RedirectStandardOutput || !start.RedirectStandardError)
            throw new ArgumentException("Background startup requires UseShellExecute=false and redirected stdout/stderr", nameof(start));
        if (!string.IsNullOrEmpty(start.Arguments)) throw new ArgumentException("Background startup requires ArgumentList instead of Arguments", nameof(start));
        if (!string.IsNullOrEmpty(start.UserName)) throw new ArgumentException("Background startup does not support alternate credentials", nameof(start));
        if (!Path.IsPathFullyQualified(start.FileName) || start.FileName.Contains('\0'))
            throw new ArgumentException("Background startup requires an explicit absolute executable path", nameof(start));
        var command = CommandLine(start.FileName, start.ArgumentList).ToCharArray();
        var environment = EnvironmentBlock(start.Environment);
        var workingDirectory = string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory;
        if (workingDirectory?.Contains('\0') == true) throw new ArgumentException("Working directory contains a NUL character", nameof(start));

        SafeFileHandle? outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, inputRead = null, inputWrite = null;
        nint attributes = 0, nativeProcess = 0, nativeThread = 0;
        var attributesInitialized = false;
        uint processId = 0;
        Process? process = null;
        StreamReader? output = null, error = null;
        try
        {
            (outputRead, outputWrite) = Pipe(parentReads: true);
            (errorRead, errorWrite) = Pipe(parentReads: true);
            (inputRead, inputWrite) = Pipe(parentReads: false);
            inputWrite.Dispose(); inputWrite = null; // The child gets EOF without waiting for this owner to exit.

            nuint attributeBytes = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeBytes);
            if (attributeBytes == 0) throw WinError("Could not size the process handle-list attributes");
            attributes = (nint)NativeMemory.AllocZeroed(attributeBytes);
            if (InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeBytes) == 0) throw WinError("Could not initialize process handle-list attributes");
            attributesInitialized = true;
            nint* inherited = stackalloc nint[3] { inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), errorWrite.DangerousGetHandle() };
            if (UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, inherited, (nuint)(3 * sizeof(nint)), 0, 0) == 0)
                throw WinError("Could not configure inherited stdio handles");
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)sizeof(StartupInfoEx), Flags = StartfUseShowWindow | StartfUseStdHandles | StartfForceOffFeedback,
                    ShowWindow = 4, StandardInput = inherited[0], StandardOutput = inherited[1], StandardError = inherited[2],
                },
                AttributeList = attributes,
            };
            var flags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
            if (start.CreateNoWindow) flags |= CreateNoWindow;
            fixed (char* executable = start.FileName, commandPointer = command, environmentPointer = environment, directory = workingDirectory)
            {
                if (CreateProcess(executable, commandPointer, 0, 0, 1, flags, environmentPointer, directory, ref startup, out var created) == 0)
                    throw WinError($"Could not start '{start.FileName}'");
                nativeProcess = created.Process; nativeThread = created.Thread; processId = created.ProcessId;
            }
            // Capture a separate managed process handle before the child can exit.
            // Process.GetProcessById alone can lose very short-lived processes.
            process = Process.GetProcessById(checked((int)processId));
            _ = process.Handle;
            output = new StreamReader(new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false), start.StandardOutputEncoding ?? Console.OutputEncoding, true);
            outputRead = null;
            error = new StreamReader(new FileStream(errorRead, FileAccess.Read, 4096, isAsync: false), start.StandardErrorEncoding ?? Console.OutputEncoding, true);
            errorRead = null;
            outputWrite.Dispose(); outputWrite = null;
            errorWrite.Dispose(); errorWrite = null;
            inputRead.Dispose(); inputRead = null;
            var owned = new BackgroundProcess(process, output, error);
            if (ResumeThread(nativeThread) == uint.MaxValue) throw WinError("Could not resume the owned background process");
            process = null; output = null; error = null;
            return owned;
        }
        catch (Exception primary)
        {
            if (processId != 0) primary.Data["backgroundProcessId"] = processId;
            if (nativeProcess != 0)
            {
                try
                {
                    var state = WaitForSingleObject(nativeProcess, 0);
                    if (state == WaitFailed) throw WinError("Could not query the failed background process");
                    if (state == WaitTimeout)
                    {
                        if (TerminateProcess(nativeProcess, uint.MaxValue) == 0) throw WinError("Could not terminate the failed background process");
                        if (WaitForSingleObject(nativeProcess, 5000) != WaitObject0) throw new IOException("Failed background process did not terminate");
                    }
                }
                catch (Exception cleanup) { AttachCleanup(primary, cleanup); }
            }
            void Cleanup(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception cleanup) { AttachCleanup(primary, cleanup); }
            }
            Cleanup(output); Cleanup(error); Cleanup(process);
            throw;
        }
        finally
        {
            if (nativeThread != 0) _ = CloseHandle(nativeThread);
            if (nativeProcess != 0) _ = CloseHandle(nativeProcess);
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) NativeMemory.Free((void*)attributes);
            outputRead?.Dispose(); outputWrite?.Dispose(); errorRead?.Dispose(); errorWrite?.Dispose(); inputRead?.Dispose(); inputWrite?.Dispose();
        }
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) Pipe(bool parentReads)
    {
        var security = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), InheritHandle = 1 };
        if (CreatePipe(out var read, out var write, ref security, 0) == 0) throw WinError("Could not create a child stdio pipe");
        var reader = new SafeFileHandle(read, ownsHandle: true); var writer = new SafeFileHandle(write, ownsHandle: true);
        try
        {
            if (SetHandleInformation(parentReads ? read : write, HandleFlagInherit, 0) == 0) throw WinError("Could not disable parent pipe inheritance");
            return (reader, writer);
        }
        catch { reader.Dispose(); writer.Dispose(); throw; }
    }

    private static string CommandLine(string executable, IEnumerable<string> arguments)
    {
        var result = new StringBuilder();
        foreach (var value in new[] { executable }.Concat(arguments))
        {
            if (value.Contains('\0')) throw new ArgumentException("Process argument contains a NUL character");
            if (result.Length != 0) result.Append(' ');
            result.Append('"');
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0; result.Append(character);
            }
            result.Append('\\', slashes * 2); result.Append('"');
        }
        if (result.Length >= 32767) throw new ArgumentException("Windows process command line exceeds 32766 UTF-16 characters");
        result.Append('\0');
        return result.ToString();
    }

    private static string EnvironmentBlock(IDictionary<string, string?> environment)
    {
        var result = new StringBuilder();
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            // ProcessStartInfo treats a null value as removal, while an empty
            // string remains an explicitly present empty environment variable.
            if (pair.Value == null) continue;
            var driveVariable = pair.Key.Length == 3 && pair.Key[0] == '=' && char.IsAsciiLetter(pair.Key[1]) && pair.Key[2] == ':';
            if (pair.Key.Length == 0 || pair.Key.Contains('\0') || pair.Key.Contains('=') && !driveVariable || pair.Value?.Contains('\0') == true)
                throw new ArgumentException("Process environment contains an invalid name or NUL character");
            result.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }
        result.Append('\0');
        if (result.Length == 1) result.Append('\0');
        return result.ToString();
    }

    internal static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (primary.Data["cleanupErrors"] is not List<string> errors) primary.Data["cleanupErrors"] = errors = [];
        errors.Add(cleanup.Message);
    }

    private static Win32Exception WinError(string operation)
    {
        var code = Marshal.GetLastPInvokeError();
        return new Win32Exception(code, $"{operation}: {new Win32Exception(code).Message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public uint Length; public nint Descriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes; public nint Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public nint AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    private static extern int CreateProcess(char* application, char* command, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint creationFlags, char* environment, char* directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePipe(out nint read, out nint write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value, nuint bytes, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CloseHandle(nint handle);
}
