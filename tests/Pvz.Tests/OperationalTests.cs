using Microsoft.EntityFrameworkCore;
using Pvz.Core;
using Pvz.Infrastructure;
using Pvz.Infrastructure.Data;
using Pvz.Infrastructure.Services;

namespace Pvz.Tests;

public sealed class OperationalTests
{
    [Fact]
    public async Task ConcurrentIssueCallsProduceOneTransition()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        async Task<bool> Attempt()
        {
            try { await store.Orders.IssueAsync(id, true); return true; }
            catch (BusinessException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt));
        Assert.Single(results, x => x);
        var order = await store.Orders.GetAsync(id);
        Assert.Equal(OrderStatus.Issued, order.Summary.Status);
        Assert.Equal(2, order.History.Count);
    }

    [Fact]
    public async Task ConcurrentDemotionsCannotRemoveAllAdministrators()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var secondId = await store.Admin.CreateEmployeeAsync(new("Второй", "second", EmployeeRole.Administrator, true), TestStore.Password);
        var session = new Session();
        var access = new AccessService(store.Factory, session);
        var admin = new AdministrationService(store.Factory, session);
        await access.LoginAsync("second", TestStore.Password);
        async Task<bool> Demote(AdministrationService service, int id, string login)
        {
            try { await service.UpdateEmployeeAsync(id, new("Имя", login, EmployeeRole.Operator, true)); return true; }
            catch (BusinessException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Demote(store.Admin, 1, "admin")), Task.Run(() => Demote(admin, secondId, "second")));
        Assert.Single(results, x => x);
        await using var db = store.Factory.CreateDbContext();
        Assert.Equal(1, await db.Employees.CountAsync(x => x.IsActive && x.Role == EmployeeRole.Administrator));
    }

    [Fact]
    public void SameDatabaseIsLockedAcrossThreadsAndDifferentDatabaseIsAllowed()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        using var first = new SingleInstanceLock(path);
        Assert.True(first.Acquired);
        bool? secondAcquired = null;
        bool? differentAcquired = null;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceLock(path);
            using var different = new SingleInstanceLock(path + "-different");
            secondAcquired = second.Acquired;
            differentAcquired = different.Acquired;
        });
        thread.Start();
        thread.Join();
        Assert.False(secondAcquired);
        Assert.True(differentAcquired);
    }

    [Fact]
    public async Task ExistingDatabaseIsBackedUpBeforeMigration()
    {
        await using var store = new TestStore();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.FilePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE LegacyMarker (Id INTEGER PRIMARY KEY); INSERT INTO LegacyMarker VALUES (7);";
            await command.ExecuteNonQueryAsync();
        }
        await new DatabaseInitializer(store.Factory, store.FilePath).InitializeAsync();
        var backup = Assert.Single(Directory.GetDirectories(store.Folder, "test.db.backup-*"));
        await using var copy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(backup, "test.db")};Pooling=False");
        await copy.OpenAsync();
        await using var check = copy.CreateCommand();
        check.CommandText = "SELECT Id FROM LegacyMarker";
        Assert.Equal(7L, await check.ExecuteScalarAsync());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'Employees'";
        Assert.Equal(0L, await check.ExecuteScalarAsync());
    }
}
