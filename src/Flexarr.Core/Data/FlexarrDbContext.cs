using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Data;

public sealed class FlexarrDbContext(DbContextOptions<FlexarrDbContext> options) : DbContext(options)
{
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<LibraryFolder> LibraryFolders => Set<LibraryFolder>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<MediaStream> MediaStreams => Set<MediaStream>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        // Readable in the database and stable if enum members are reordered.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Library>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(200);
            e.HasMany(l => l.Folders).WithOne().HasForeignKey(f => f.LibraryId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany<MediaItem>().WithOne().HasForeignKey(i => i.LibraryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LibraryFolder>().HasIndex(f => f.Path).IsUnique();

        modelBuilder.Entity<MediaItem>(e =>
        {
            e.HasOne(i => i.Parent).WithMany(i => i.Children).HasForeignKey(i => i.ParentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(i => i.Files).WithOne(f => f.MediaItem).HasForeignKey(f => f.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => new { i.LibraryId, i.ParentId, i.SortTitle });
            e.HasIndex(i => i.Path);
        });

        modelBuilder.Entity<MediaFile>(e =>
        {
            e.HasIndex(f => f.Path).IsUnique();
            e.HasMany(f => f.Streams).WithOne().HasForeignKey(s => s.MediaFileId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
