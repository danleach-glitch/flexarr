using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Flexarr.Core.Paths;

/// <summary>What sort of file system a folder is on.</summary>
public static class FolderKind
{
    /// <summary>
    /// File systems that often don't report changes made from elsewhere: network shares (another machine writes to the
    /// NAS), the shared folders of Docker Desktop and other virtual machines (the host writes to them), and pools of disks
    /// such as Unraid's and mergerfs (a file written straight to one of the disks).
    /// </summary>
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "smbfs", "cifs", "smb3", "smb2", "nfs", "nfs4", "afpfs", "webdav", "davfs", "fuse.sshfs", "sshfs", "fuse.rclone", "9p",
        "virtiofs", "fakeowner", "grpcfuse", "fuse.grpcfuse", "osxfs", "vboxsf", "prl_fs", "fuse.glusterfs", "ceph", "fuse.ceph",
        "fuse.mergerfs", "mergerfs", "fuse.shfs", "shfs", "fuse.unionfs", "fuse.unionfs-fuse", "unionfs",
    };

    /// <summary>
    /// Whether <paramref name="path"/> is on a network share or a virtual machine's shared folder, going by the mount
    /// it's on. Reads the system's list of mounts and, on Linux, follows the path: not for inside a lock.
    /// </summary>
    public static bool IsShare(string path)
    {
        try
        {
            // Where it really is: a link onto a NAS is on the NAS, and macOS doesn't mind the case of /Volumes/media.
            var full = Resolved(path);
            var mount = DriveInfo.GetDrives()
                .Where(d => PathComparison.IsUnder(full, d.Name, PathComparison.FileSystemComparison))
                .MaxBy(d => d.Name.Length);
            if (mount is null)
            {
                return false;
            }

            return IsShareType(mount.DriveType, mount.DriveFormat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsShareType(DriveType type, string format) => type == DriveType.Network || Shared.Contains(format);

    /// <summary><paramref name="path"/> with every symbolic link along it followed.</summary>
    internal static string Resolved(string path)
    {
        var current = Path.GetFullPath(path);
        for (var links = 0; links < 40; links++)
        {
            var root = Path.GetPathRoot(current) ?? "/";
            var built = root;
            string? followed = null;
            var segments = current[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length && followed is null; i++)
            {
                var next = Path.Combine(built, segments[i]);
                if (new DirectoryInfo(next).LinkTarget is { } target)
                {
                    followed = Path.GetFullPath(Path.Combine([built, target, .. segments[(i + 1)..]]));
                }

                built = next;
            }

            if (followed is null)
            {
                return built;
            }

            current = followed;
        }

        return current; // A loop of links: nothing can be on it anyway.
    }

    /// <summary>
    /// What tells this folder from another later at the same path: a drive unmounted or mounted again, or the folder
    /// deleted and made afresh. Null if it isn't there. Linux's watches stay with the folder they were put on, so a new one
    /// needs a new watch; there, it's the mount, device, inode and birth time; elsewhere, its creation time. Asks only
    /// about the folder itself, which a watch keeps in the system's memory, so it doesn't wake the drive.
    /// </summary>
    public static string? Identity(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            return (OperatingSystem.IsLinux() ? LinuxIdentity(path) : null)
                ?? Directory.GetCreationTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? LinuxIdentity(string path)
    {
        const int AtFdCwd = -100;
        const uint Wanted = 0x100 | 0x800 | 0x1000; // STATX_INO, STATX_BTIME and STATX_MNT_ID.
        var statx = new byte[256];
        try
        {
            if (StatX(AtFdCwd, path, 0, Wanted, statx) != 0)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // Not glibc.
        }

        // The struct's layout is the same on every architecture: stx_ino at 32, stx_btime at 80, stx_dev_major and minor at 136, stx_mnt_id at 144.
        var span = statx.AsSpan();
        return string.Create(CultureInfo.InvariantCulture,
            $"{BinaryPrimitives.ReadUInt64LittleEndian(span[144..])}:{BinaryPrimitives.ReadUInt32LittleEndian(span[136..])}:{BinaryPrimitives.ReadUInt32LittleEndian(span[140..])}:{BinaryPrimitives.ReadUInt64LittleEndian(span[32..])}:{BinaryPrimitives.ReadInt64LittleEndian(span[80..])}:{BinaryPrimitives.ReadUInt32LittleEndian(span[88..])}");
    }

#pragma warning disable SYSLIB1054, CA5392, CA2101 // Plain marshalling is all this needs, without allowing unsafe code in this assembly for one call.
    [DllImport("libc.so.6", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int StatX(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] result);
#pragma warning restore SYSLIB1054, CA5392, CA2101
}
