using Microsoft.EntityFrameworkCore;
using Pvz.Core;

namespace Pvz.Tests;

public sealed class SqliteConstraintTests
{
    [Fact]
    public async Task UniqueLoginIsEnforcedByDatabase()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        await using var db = store.Factory.CreateDbContext();
        db.Employees.Add(new() { Name = "Имя", Login = "ADMIN", NormalizedLogin = "ADMIN", PasswordHash = "test-hash", Role = EmployeeRole.Operator });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("UPDATE OrderItems SET Quantity = 0")]
    [InlineData("UPDATE Orders SET StorageLocationId = NULL")]
    [InlineData("UPDATE Orders SET StorageLocationId = 99999")]
    [InlineData("UPDATE Orders SET StorageUntil = ReceivedAt")]
    [InlineData("UPDATE Orders SET Status = 99")]
    [InlineData("UPDATE Orders SET Number = ' lowercase '")]
    [InlineData("DELETE FROM Employees")]
    [InlineData("DELETE FROM Orders")]
    [InlineData("DELETE FROM StorageLocations")]
    public async Task CheckAndForeignKeyConstraintsRejectInvalidDirectSql(string sql)
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        await store.RegisterAsync();
        await using var db = store.Factory.CreateDbContext();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql));
    }

    [Fact]
    public async Task OrderNumberAndLocationCompositeIndexAreReallyUnique()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        await store.RegisterAsync("ONE");
        await store.RegisterAsync("TWO");
        await using var db = store.Factory.CreateDbContext();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE Orders SET Number = 'ONE' WHERE Number = 'TWO'"));
        db.StorageLocations.Add(new() { Rack = "A", Cell = "01" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
