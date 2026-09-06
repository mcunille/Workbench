// Copyright (c) 2026 The White Stag Collection.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Workbench.Server.Storage;

// Open each ancestor without following links. Windows handles deny rename/delete;
// Linux child operations are relative to the pinned directory descriptor.
internal sealed class ConfinedDirectory : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];
    private readonly string _root;
    private const int LinuxDirectoryFlags = 0x10000 | 0x20000 | 0x80000;

    public ConfinedDirectory(string root)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new IOException("Storage requires an absolute root.");
        }
        _root = Path.GetFullPath(root);
        var volume = Path.GetPathRoot(_root)!;
        if (OperatingSystem.IsWindows() && (volume.Length != 3 || volume[1] != ':'))
        {
            throw new IOException("Storage requires a local filesystem volume.");
        }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Confined storage supports Windows and Linux.");
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var path = volume;
                _directories.Add(OpenWindows(path, 0x80, 3, 0x02000000, directory: true));
                foreach (var segment in _root[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    path = Path.Combine(path, segment);
                    _directories.Add(OpenWindows(path, 0x80, 3, 0x02000000, directory: true));
                }
            }
            else
            {
                _directories.Add(OpenLinux(-100, "/", LinuxDirectoryFlags));
                foreach (var segment in _root[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    _directories.Add(OpenLinux(Descriptor, segment, LinuxDirectoryFlags));
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private int Descriptor => _directories[^1].DangerousGetHandle().ToInt32();

    public FileStream Open(string name, bool create)
    {
        ValidateName(name);
        var handle = OperatingSystem.IsWindows()
            ? OpenWindows(Path.Combine(_root, name), create ? 0x40000000u : 0x80000000u, create ? 1u : 3u, 0, directory: false)
            : OpenLinux(Descriptor, name, 0x20000 | 0x80000 | 0x800 | (create ? 1 | 0x40 | 0x80 : 0));
        try
        {
            return new FileStream(handle, create ? FileAccess.Write : FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool Exists(string name)
    {
        try
        {
            using var stream = Open(name, create: false);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public void Publish(string source, string destination)
    {
        ValidateName(source);
        ValidateName(destination);
        if (OperatingSystem.IsWindows())
        {
            File.Move(Path.Combine(_root, source), Path.Combine(_root, destination), overwrite: false);
        }
        else if (RenameAt2(Descriptor, source, Descriptor, destination, 1) != 0)
        {
            ThrowIo(Marshal.GetLastPInvokeError());
        }
        Synchronize();
    }

    public void Synchronize()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        // Flushing file bytes does not persist a renamed or removed directory entry.
        while (Fsync(Descriptor) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 4) // EINTR: no durability acknowledgement until the retry succeeds.
            {
                ThrowIo(error);
            }
        }
    }

    public void Delete(string name)
    {
        ValidateName(name);
        if (OperatingSystem.IsWindows())
        {
            File.Delete(Path.Combine(_root, name));
        }
        else if (UnlinkAt(Descriptor, name, 0) != 0 && Marshal.GetLastPInvokeError() != 2)
        {
            ThrowIo(Marshal.GetLastPInvokeError());
        }
    }

    public IEnumerable<string> Names() => Directory.EnumerateFiles(
        OperatingSystem.IsLinux() ? $"/proc/self/fd/{Descriptor}" : _root)
        .Select(path => Path.GetFileName(path));

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 100 || name.Any(character =>
            !char.IsAsciiHexDigit(character) && character is not '-' and not '.'))
        {
            throw new IOException("Invalid storage object name.");
        }
    }

    private static SafeFileHandle OpenLinux(int directory, string name, int flags)
    {
        var descriptor = OpenAt(directory, name, flags, 0x180); // 0600
        if (descriptor < 0)
        {
            ThrowIo(Marshal.GetLastPInvokeError());
        }
        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

    private static SafeFileHandle OpenWindows(string path, uint access, uint disposition, uint flags, bool directory)
    {
        var handle = CreateFile(path, access, 3, IntPtr.Zero, disposition, flags | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            ThrowIo(error);
        }
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8) ||
            (attributes.Attributes & 0x400) != 0 || ((attributes.Attributes & 0x10) != 0) != directory)
        {
            handle.Dispose();
            throw new IOException("Storage links and unexpected object types are prohibited.");
        }
        return handle;
    }

    private static void ThrowIo(int error)
    {
        if (error == 2 || (OperatingSystem.IsWindows() && error == 3))
        {
            throw new FileNotFoundException("Storage object is missing.");
        }
        throw new IOException("Storage access failed.");
    }

    public void Dispose()
    {
        for (var index = _directories.Count - 1; index >= 0; index--)
        {
            _directories[index].Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, int mode);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int sourceDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        int targetDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, uint flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
}
