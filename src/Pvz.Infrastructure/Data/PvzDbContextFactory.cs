using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pvz.Infrastructure.Data;

public sealed class PvzDbContextFactory(DbContextOptions<PvzDbContext> options) : IDbContextFactory<PvzDbContext>
{
    public PvzDbContext CreateDbContext() => new(options);

    public static PvzDbContextFactory ForFile(string path)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path), ForeignKeys = true, Pooling = false, DefaultTimeout = 10
        };
        return new(new DbContextOptionsBuilder<PvzDbContext>().UseSqlite(connection.ToString()).Options);
    }
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PvzDbContext>
{
    public PvzDbContext CreateDbContext(string[] args) =>
        PvzDbContextFactory.ForFile(Path.Combine(Path.GetTempPath(), "pvz-design-only.db")).CreateDbContext();
}
