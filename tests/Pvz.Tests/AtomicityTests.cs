using Microsoft.EntityFrameworkCore;
using Pvz.Core;

namespace Pvz.Tests;

public sealed class AtomicityTests
{
    [Fact]
    public async Task FailedInitialHistoryInsertRollsBackWholeRegistration()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        await RejectHistoryAsync(store);
        await Assert.ThrowsAsync<BusinessException>(() => store.RegisterAsync());
        await using var db = store.Factory.CreateDbContext();
        Assert.Empty(await db.Orders.ToListAsync());
        Assert.Empty(await db.OrderItems.ToListAsync());
        Assert.Empty(await db.OrderStatusHistory.ToListAsync());
    }

    [Theory]
    [InlineData(OrderStatus.AwaitingPickup)]
    [InlineData(OrderStatus.Issued)]
    [InlineData(OrderStatus.PendingReturn)]
    public async Task FailedHistoryInsertRollsBackEveryReturnRoute(OrderStatus initial)
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        if (initial == OrderStatus.Issued) await store.Orders.IssueAsync(id, true);
        if (initial == OrderStatus.PendingReturn) await store.Orders.StartReturnAsync(id, "Причина", store.LocationId, true);
        var before = await store.Orders.GetAsync(id);
        await RejectHistoryAsync(store);
        if (initial == OrderStatus.PendingReturn)
            await Assert.ThrowsAsync<BusinessException>(() => store.Orders.CompleteReturnAsync(id, true));
        else
            await Assert.ThrowsAsync<BusinessException>(() => store.Orders.StartReturnAsync(id, "Возврат", store.LocationId, true));
        var actual = await store.Orders.GetAsync(id);
        Assert.Equal(before.Summary, actual.Summary);
        Assert.Equal(before.StorageLocationId, actual.StorageLocationId);
        Assert.Equal(before.Items.ToArray(), actual.Items.ToArray());
        Assert.Equal(before.History.ToArray(), actual.History.ToArray());
    }

    [Fact]
    public async Task FailedReplacementItemInsertRestoresOriginalItemsAndRecipient()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        var before = await store.Orders.GetAsync(id);
        var other = await store.Admin.SaveLocationAsync(null, "B", "02");
        await using (var db = store.Factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailItem BEFORE INSERT ON OrderItems BEGIN SELECT RAISE(ABORT, 'simulated item failure'); END;");
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.EditAsync(id,
            new("Не сохранено", "12345", other, [new("Новая позиция", 9)])));
        var actual = await store.Orders.GetAsync(id);
        Assert.Equal(before.Summary, actual.Summary);
        Assert.Equal(before.StorageLocationId, actual.StorageLocationId);
        Assert.Equal(before.Items.ToArray(), actual.Items.ToArray());
        Assert.Equal(before.History.ToArray(), actual.History.ToArray());
    }

    private static async Task RejectHistoryAsync(TestStore store)
    {
        await using var db = store.Factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailHistory BEFORE INSERT ON OrderStatusHistory BEGIN SELECT RAISE(ABORT, 'simulated history failure'); END;");
    }
}
