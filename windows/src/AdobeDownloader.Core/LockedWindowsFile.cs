using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AdobeDownloader.Core;

/// <summary>Holds a Windows file against writes/deletion and its ancestor directories against rename.
/// Rejects reparse points using the opened handles. This is not signature or package verification.</summary>
public sealed class LockedWindowsFile : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    public string Path { get; }
    public FileStream Stream { get; private set; } = null!;
    private LockedWindowsFile(string path) => Path = path;
    public static LockedWindowsFile Open(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        path = System.IO.Path.GetFullPath(path);
        if (path.Length < 4 || path.Length > 32000 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new InvalidDataException("A local absolute file path is required.");
        foreach (var part in path[3..].Split('\\')) PackageDownloader.ValidateFileName(part);
        var lease = new LockedWindowsFile(path);
        try
        {
            var parents = new Stack<string>();
            for (var parent = System.IO.Path.GetDirectoryName(path); parent is not null; parent = System.IO.Path.GetDirectoryName(parent)) parents.Push(parent);
            foreach (var parent in parents)
            {
                // FILE_READ_ATTRIBUTES; share read/write but not delete; OPEN_EXISTING;
                // BACKUP_SEMANTICS | OPEN_REPARSE_POINT avoids following the final directory component.
                var handle = CreateFileW(parent, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
                lease.directories.Add(handle); Validate(handle, directory: true);
            }
            // Use OPEN_REPARSE_POINT for the file as well, avoiding an open through a substituted symlink.
            var file = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (file.IsInvalid) { var error = Marshal.GetLastWin32Error(); file.Dispose(); throw new Win32Exception(error); }
            try { Validate(file, directory: false); lease.Stream = new FileStream(file, FileAccess.Read); }
            catch { file.Dispose(); throw; }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose()
    {
        Stream?.Dispose();
        for (var index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
        directories.Clear();
    }
    private static void Validate(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var information, (uint)Marshal.SizeOf<AttributeTagInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) != 0) != directory)
            throw new InvalidDataException("Execution paths cannot contain reparse points or unexpected file types.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTagInfo { public uint Attributes, ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTagInfo information, uint length);
}
