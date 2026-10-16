using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class Junction
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputLength, nint output, uint outputLength, out uint returned, nint overlapped);

    internal static void Create(string link, string target)
    {
        Directory.CreateDirectory(link);
        var fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + fullTarget);
        var print = Encoding.Unicode.GetBytes(fullTarget);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xa0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 16 + substitute.Length + 2);
        using var handle = CreateFileW(link, 0x40000000, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900a4, buffer, (uint)buffer.Length, 0, 0, out _, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "could not create the owned profile-escape junction");
    }
}
