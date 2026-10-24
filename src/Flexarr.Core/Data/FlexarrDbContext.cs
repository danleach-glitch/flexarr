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
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Credit> Credits => Set<Credit>();
    public DbSet<PlaybackState> PlaybackStates => Set<PlaybackState>();
    public DbSet<MediaMarker> MediaMarkers => Set<MediaMarker>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();

    /// <summary>
    /// Libraries hidden from the profile this context serves. Libraries and their items in them are filtered out of
    /// every query (use <c>IgnoreQueryFilters</c> to see them). Empty for background work, which sees everything.
    /// Items and files whose files went missing from disk (<see cref="MediaItem.MissingSince"/>) are filtered out too.
    /// </summary>
    public IReadOnlyList<Guid> HiddenLibraryIds { get; set; } = [];

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
            e.HasQueryFilter(l => !HiddenLibraryIds.Contains(l.Id));
        });

        modelBuilder.Entity<LibraryFolder>().HasIndex(f => f.Path).IsUnique();

        modelBuilder.Entity<MediaItem>(e =>
        {
            // Every item (seasons and episodes too) carries its library, so hiding one hides all of it.
            e.HasQueryFilter(i => !HiddenLibraryIds.Contains(i.LibraryId) && i.MissingSince == null);
            e.HasOne(i => i.Parent).WithMany(i => i.Children).HasForeignKey(i => i.ParentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(i => i.Files).WithOne(f => f.MediaItem).HasForeignKey(f => f.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => new { i.LibraryId, i.ParentId, i.SortTitle });
            e.HasIndex(i => i.Path);
            e.HasIndex(i => i.AddedAt);
            e.HasMany(i => i.Genres).WithMany(g => g.Items).UsingEntity(j => j.ToTable("MediaItemGenres"));
            e.HasMany(i => i.Credits).WithOne(c => c.MediaItem).HasForeignKey(c => c.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Genre>().HasIndex(g => g.Name).IsUnique();

        modelBuilder.Entity<Person>(e =>
        {
            e.HasIndex(p => p.TmdbId);
            e.HasIndex(p => p.TvMazeId);
            e.HasIndex(p => p.Name);
            e.HasMany(p => p.Credits).WithOne(c => c.Person).HasForeignKey(c => c.PersonId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MediaFile>(e =>
        {
            e.HasIndex(f => f.Path).IsUnique();
            e.HasQueryFilter(f => f.MissingSince == null);
            e.HasMany(f => f.Streams).WithOne().HasForeignKey(s => s.MediaFileId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(f => f.Markers).WithOne().HasForeignKey(m => m.MediaFileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Profile>(e =>
        {
            e.Property(p => p.Id).HasMaxLength(64);
            e.Property(p => p.Name).HasMaxLength(60);
            e.HasIndex(p => p.TailscaleLogin).IsUnique();
            e.HasData(new Profile { Id = PlaybackState.DefaultUser, Name = "Main profile", CreatedAt = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc) });
        });

        modelBuilder.Entity<Collection>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(120);
            e.Property(c => c.OwnerProfileId).HasMaxLength(64);
            // A profile's personal collections go with it.
            e.HasOne<Profile>().WithMany().HasForeignKey(c => c.OwnerProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Items).WithOne().HasForeignKey(i => i.CollectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.TmdbCollectionId);
        });

        modelBuilder.Entity<CollectionItem>(e =>
        {
            e.HasKey(i => new { i.CollectionId, i.MediaItemId });
            // A title that leaves the library leaves its collections.
            e.HasOne(i => i.MediaItem).WithMany().HasForeignKey(i => i.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlaybackState>(e =>
        {
            e.HasKey(p => new { p.UserId, p.MediaItemId });
            e.Property(p => p.UserId).HasMaxLength(64);
            e.HasOne(p => p.MediaItem).WithMany(i => i.PlaybackStates).HasForeignKey(p => p.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Profile>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.UserId, p.LastPlayedAt });
        });
    }
}
