using Flexarr.Core.Paths;

namespace Flexarr.Core.Tests;

public class FolderKindTests
{
    [Theory]
    [InlineData(DriveType.Network, "nfs4", true)]
    [InlineData(DriveType.Unknown, "fuse.mergerfs", true)]
    [InlineData(DriveType.Unknown, "fuse.shfs", true)]
    [InlineData(DriveType.Unknown, "virtiofs", true)]
    [InlineData(DriveType.Fixed, "ext4", false)]
    [InlineData(DriveType.Network, "fuseblk", false)]
    [InlineData(DriveType.Fixed, "apfs", false)]
    public void WhichAreShares(DriveType type, string format, bool share) => Assert.Equal(share, FolderKind.IsShareType(type, format));

    [Fact]
    public void Links_AreFollowed_AllAlongThePath()
    {
        var scratch = FolderKind.Resolved(Directory.CreateTempSubdirectory("folderkind").FullName);
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(scratch, "real", "films")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(scratch, "link"), Path.Combine(scratch, "real"));
            Directory.CreateSymbolicLink(Path.Combine(scratch, "relative"), "real");

            Assert.Equal(real, FolderKind.Resolved(Path.Combine(scratch, "link", "films")));
            Assert.Equal(real, FolderKind.Resolved(Path.Combine(scratch, "relative", "films")));
            Assert.Equal(real, FolderKind.Resolved(real));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void Identity_StaysTheSame_AndGoesWithTheFolder()
    {
        var folder = Directory.CreateTempSubdirectory("folderkind").FullName;
        var identity = FolderKind.Identity(folder);
        Assert.NotNull(identity);
        Assert.Equal(identity, FolderKind.Identity(folder));

        Directory.Delete(folder);
        Assert.Null(FolderKind.Identity(folder));
    }
}
