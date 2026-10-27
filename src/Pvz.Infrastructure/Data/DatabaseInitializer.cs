using Microsoft.EntityFrameworkCore;

namespace Pvz.Infrastructure.Data;

public sealed class DatabaseInitializer(PvzDbContextFactory factory, string databasePath)
{
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        await using var db = factory.CreateDbContext();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        if (pending.Length == 0) return;

        // The application instance lock is held before entering this method.
        // Copy the closed database and its journal companions before changing its schema.
        if (File.Exists(databasePath))
        {
            var backup = databasePath + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            Directory.CreateDirectory(backup);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var source = databasePath + suffix;
                if (File.Exists(source)) File.Copy(source, Path.Combine(backup, Path.GetFileName(source)));
            }
        }
        await db.Database.MigrateAsync();
    }
}
