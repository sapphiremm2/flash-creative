using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AdobeDownloader.Core;

public sealed record ShortcutArtifact(string Path, string Sha256, string Target, string WorkingDirectory);

/// <summary>Creates a new staged Windows shortcut. Does not launch targets or write Start Menu folders.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsShortcut
{
    public static Task<ShortcutArtifact> StageAsync(string target, string destination, CancellationToken ct = default)
    {
        target = LocalPath(target); destination = LocalPath(destination);
        if (!destination.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.GetDirectoryName(destination)))
            throw new InvalidDataException("Shortcut staging needs an existing parent and a .lnk destination.");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Shortcut destination must be new.");
        var completion = new TaskCompletionSource<ShortcutArtifact>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? instance = null; ShortcutArtifact? result = null; Exception? failure = null;
            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".flash-link-" + Guid.NewGuid().ToString("N") + ".lnk");
            try
            {
                ct.ThrowIfCancellationRequested();
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
                var link = (IShellLinkW)instance;
                var working = Path.GetDirectoryName(target)!;
                link.SetPath(target); link.SetWorkingDirectory(working); link.SetArguments(""); link.SetShowCmd(1);
                ((IPersistFile)instance).Save(temporary, true);
                // Load our own persisted artifact without Resolve (which may search or show UI).
                ((IPersistFile)instance).Load(temporary, 0);
                var actual = new StringBuilder(32768); link.GetPath(actual, actual.Capacity, IntPtr.Zero, 4);
                var arguments = new StringBuilder(32768); link.GetArguments(arguments, arguments.Capacity);
                var directory = new StringBuilder(32768); link.GetWorkingDirectory(directory, directory.Capacity);
                if (!actual.ToString().Equals(target, StringComparison.OrdinalIgnoreCase) || arguments.Length != 0 ||
                    !directory.ToString().Equals(working, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Persisted shortcut differs from the requested target.");
                string hash;
                using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                { if (file.Length > 1024 * 1024) throw new InvalidDataException("Shortcut exceeds size limit."); file.Flush(true); hash = Convert.ToHexString(SHA256.HashData(file)); }
                ct.ThrowIfCancellationRequested(); LocalPath(destination); File.Move(temporary, destination, overwrite: false);
                result = new(destination, hash, target, working);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    if (instance is not null) Marshal.FinalReleaseComObject(instance);
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception ex) { failure ??= ex; }
            }
            if (failure is OperationCanceledException) completion.TrySetCanceled(ct);
            else if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult(result!);
        }) { IsBackground = true, Name = "Flash Creative shortcut staging" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static string LocalPath(string path)
    {
        if (path.Length > 32000 || path.Any(char.IsControl) || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new InvalidDataException("Expected a bounded local absolute path.");
        foreach (var part in path[3..].Split('\\')) PackageDownloader.ValidateFileName(part);
        path = Path.GetFullPath(path);
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Shortcut paths cannot contain reparse points."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
        return path;
    }
    // Vtable order is the native IShellLinkW order; HRESULT failures are translated by COM interop.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
