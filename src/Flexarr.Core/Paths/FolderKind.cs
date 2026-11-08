namespace Flexarr.Core.Paths;

/// <summary>What sort of file system a folder is on.</summary>
public static class FolderKind
{
    /// <summary>
    /// File systems that often don't report changes made from elsewhere: network shares (another machine writes to the
    /// NAS) and the shared folders of Docker Desktop and other virtual machines (the host writes to them).
    /// </summary>
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "smbfs", "cifs", "smb3", "smb2", "nfs", "nfs4", "afpfs", "webdav", "davfs", "fuse.sshfs", "sshfs", "fuse.rclone", "9p",
        "virtiofs", "fakeowner", "grpcfuse", "fuse.grpcfuse", "osxfs", "vboxsf", "prl_fs", "fuse.glusterfs", "ceph", "fuse.ceph",
    };

    /// <summary>
    /// Whether <paramref name="path"/> is on a network share or a virtual machine's shared folder, going by the mount
    /// it's on. Reads the system's list of mounts and, on Linux, follows the path: not for inside a lock.
    /// </summary>
    public static bool IsShare(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var mount = DriveInfo.GetDrives()
                .Where(d => PathComparison.IsUnder(full, d.Name))
                .MaxBy(d => d.Name.Length);
            if (mount is null)
            {
                return false;
            }

            return mount.DriveType == DriveType.Network || Shared.Contains(mount.DriveFormat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
