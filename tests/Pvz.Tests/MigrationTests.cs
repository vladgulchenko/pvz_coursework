using Microsoft.EntityFrameworkCore;

namespace Pvz.Tests;

public sealed class MigrationTests
{
    [Fact]
    public async Task CurrentModelDoesNotRequireAnUnrecordedMigration()
    {
        await using var store = new TestStore();
        await using var db = store.Factory.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
