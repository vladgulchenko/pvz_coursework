using Microsoft.EntityFrameworkCore;
using Pvz.Core;

namespace Pvz.Tests;

public sealed class OrderTests
{
    [Fact]
    public async Task RegisterPersistsNormalizedOrderItemsPlacementAndInitialHistoryAcrossContexts()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        var details = await store.Orders.GetAsync(id);
        Assert.Equal("TEST-001", details.Summary.Number);
        Assert.Equal(OrderStatus.AwaitingPickup, details.Summary.Status);
        Assert.Equal(store.LocationId, details.StorageLocationId);
        Assert.Equal(2, Assert.Single(details.Items).Quantity);
        var history = Assert.Single(details.History);
        Assert.Null(history.PreviousStatus);
        Assert.Equal(store.Clock.UtcNow, history.ChangedAt);
        Assert.Equal(DateTimeKind.Utc, details.Summary.ReceivedAt.Kind);
        await Assert.ThrowsAsync<BusinessException>(() => store.RegisterAsync("TEST-001"));
        Assert.Single(await store.Orders.SearchAsync());
        await using var db = store.Factory.CreateDbContext();
        Assert.Equal(1, await db.OrderItems.CountAsync());
        Assert.Equal(1, await db.OrderStatusHistory.CountAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("future")]
    [InlineData("equal")]
    [InlineData("local")]
    public async Task InvalidRegistrationLeavesNoPartialRows(string scenario)
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var input = store.Input();
        input = scenario switch
        {
            "empty" => input with { Items = [] },
            "zero" => input with { Items = [new("Книга", 0)] },
            "negative" => input with { Items = [new("Книга", -1)] },
            "future" => input with { ReceivedAt = store.Clock.UtcNow.AddMinutes(1) },
            "equal" => input with { StorageUntil = input.ReceivedAt },
            _ => input with { ReceivedAt = DateTime.SpecifyKind(input.ReceivedAt, DateTimeKind.Unspecified) }
        };
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.RegisterAsync(input));
        await using var db = store.Factory.CreateDbContext();
        Assert.Empty(await db.Orders.ToListAsync());
        Assert.Empty(await db.OrderItems.ToListAsync());
        Assert.Empty(await db.OrderStatusHistory.ToListAsync());
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task IssueChecksExactDeadlineAtWriteTime(int deltaTicks, bool allowed)
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var input = store.Input();
        var id = await store.Orders.RegisterAsync(input);
        store.Clock.UtcNow = input.StorageUntil.AddTicks(deltaTicks);
        if (allowed) await store.Orders.IssueAsync(id, true);
        else await Assert.ThrowsAsync<BusinessException>(() => store.Orders.IssueAsync(id, true));
        var order = await store.Orders.GetAsync(id);
        Assert.Equal(allowed ? OrderStatus.Issued : OrderStatus.AwaitingPickup, order.Summary.Status);
        Assert.Equal(allowed ? null : store.LocationId, order.StorageLocationId);
        Assert.Equal(allowed ? 2 : 1, order.History.Count);
        Assert.Equal(!allowed, order.Summary.IsOverdue);
    }

    [Fact]
    public async Task CancelDoesNotWriteAndSequentialIssueCannotDuplicateHistory()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        await store.Orders.IssueAsync(id, false);
        Assert.Single((await store.Orders.GetAsync(id)).History);
        await store.Orders.IssueAsync(id, true);
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.IssueAsync(id, true));
        Assert.Equal(2, (await store.Orders.GetAsync(id)).History.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothReturnRoutesRequireReasonAndKeepHistory(bool issueFirst)
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        if (issueFirst) await store.Orders.IssueAsync(id, true);
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.StartReturnAsync(id, " ", store.LocationId, true));
        if (issueFirst)
            await Assert.ThrowsAsync<BusinessException>(() => store.Orders.StartReturnAsync(id, "Причина", null, true));
        await store.Orders.StartReturnAsync(id, "Отказ получателя", issueFirst ? store.LocationId : null, true);
        var pending = await store.Orders.GetAsync(id);
        Assert.Equal(OrderStatus.PendingReturn, pending.Summary.Status);
        Assert.Equal(store.LocationId, pending.StorageLocationId);
        Assert.Equal("Отказ получателя", pending.History.Last().ReturnReason);
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.IssueAsync(id, true));
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.StartReturnAsync(id, "Причина", store.LocationId, true));
        await store.Orders.CompleteReturnAsync(id, false);
        Assert.Equal(OrderStatus.PendingReturn, (await store.Orders.GetAsync(id)).Summary.Status);
        await store.Orders.CompleteReturnAsync(id, true);
        var returned = await store.Orders.GetAsync(id);
        Assert.Equal(OrderStatus.Returned, returned.Summary.Status);
        Assert.Null(returned.StorageLocationId);
        Assert.Equal(issueFirst ? 4 : 3, returned.History.Count);
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.IssueAsync(id, true));
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.StartReturnAsync(id, "Причина", store.LocationId, true));
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.CompleteReturnAsync(id, true));
    }

    [Fact]
    public async Task CannotCompleteReturnDirectlyFromAwaitingOrIssued()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.CompleteReturnAsync(id, true));
        await store.Orders.IssueAsync(id, true);
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.CompleteReturnAsync(id, true));
    }

    [Fact]
    public async Task EditingChangesOnlyEditableFieldsAndRequiresAtLeastOneItem()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        var initial = await store.Orders.GetAsync(id);
        await store.Orders.EditAsync(id, new("Другое имя", "12345", store.LocationId, [new("Новый товар", 4)]));
        var changed = await store.Orders.GetAsync(id);
        Assert.Equal("Другое имя", changed.Summary.RecipientName);
        Assert.Equal(initial.Summary.Number, changed.Summary.Number);
        Assert.Equal(initial.Summary.ReceivedAt, changed.Summary.ReceivedAt);
        Assert.Equal(initial.Summary.StorageUntil, changed.Summary.StorageUntil);
        Assert.Single(changed.History);
        Assert.Equal(4, Assert.Single(changed.Items).Quantity);
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.EditAsync(id, new("Имя", "123", store.LocationId, [])));
        Assert.Single((await store.Orders.GetAsync(id)).Items);
        await store.Orders.IssueAsync(id, true);
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.EditAsync(id, new("Имя", "123", store.LocationId, [new("Книга", 1)])));
        await store.Orders.StartReturnAsync(id, "Причина", store.LocationId, true);
        await store.Orders.EditAsync(id, new("Возврат", "123", store.LocationId, [new("Книга", 1)]));
    }

    [Fact]
    public async Task SearchFiltersAreCombinedAndMultipleOrdersMayShareCell()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var first = await store.RegisterAsync("abc-1");
        await store.RegisterAsync("abc-2");
        await store.RegisterAsync("other");
        await store.Orders.IssueAsync(first, true);
        Assert.Equal(2, (await store.Orders.SearchAsync(" aBc ")).Count);
        Assert.Single(await store.Orders.SearchAsync("ABC", OrderStatus.AwaitingPickup));
        Assert.Empty(await store.Orders.SearchAsync("missing"));
        await Assert.ThrowsAsync<BusinessException>(() => store.Admin.DeleteLocationAsync(store.LocationId));
    }

    [Fact]
    public async Task HistoryInsertFailureRollsBackStatusAndPlacement()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        await using (var db = store.Factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailHistory BEFORE INSERT ON OrderStatusHistory BEGIN SELECT RAISE(ABORT, 'simulated history failure'); END;");
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.IssueAsync(id, true));
        var fresh = await store.Orders.GetAsync(id);
        Assert.Equal(OrderStatus.AwaitingPickup, fresh.Summary.Status);
        Assert.Equal(store.LocationId, fresh.StorageLocationId);
        Assert.Single(fresh.History);
    }
}
