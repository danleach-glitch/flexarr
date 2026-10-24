using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Flexarr.Core.Data;

/// <summary>Used by <c>dotnet ef migrations add</c>; the running server configures the context itself.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FlexarrDbContext>
{
    public FlexarrDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite("Data Source=design.db").Options);
}
