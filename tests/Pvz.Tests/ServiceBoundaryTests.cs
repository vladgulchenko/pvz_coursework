using Microsoft.EntityFrameworkCore;
using Pvz.Core;

namespace Pvz.Tests;

public sealed class ServiceBoundaryTests
{
    [Theory]
    [InlineData("search")]
    [InlineData("details")]
    [InlineData("register")]
    [InlineData("edit")]
    [InlineData("issue")]
    [InlineData("start-return")]
    [InlineData("complete-return")]
    [InlineData("locations")]
    public async Task LoggedOutUserCannotReadOrWriteProtectedData(string operation)
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        store.Access.Logout();
        Func<Task> action = operation switch
        {
            "search" => () => store.Orders.SearchAsync(),
            "details" => () => store.Orders.GetAsync(id),
            "register" => () => store.RegisterAsync("SECOND"),
            "edit" => () => store.Orders.EditAsync(id, new("Имя", "123", store.LocationId, [new("Товар", 1)])),
            "issue" => () => store.Orders.IssueAsync(id, true),
            "start-return" => () => store.Orders.StartReturnAsync(id, "Причина", store.LocationId, true),
            "complete-return" => () => store.Orders.CompleteReturnAsync(id, true),
            _ => () => store.Admin.GetLocationsAsync()
        };
        await Assert.ThrowsAsync<AccessDeniedException>(action);
        await using var db = store.Factory.CreateDbContext();
        var order = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.AwaitingPickup, order.Status);
        Assert.Equal(store.LocationId, order.StorageLocationId);
        Assert.Equal(1, await db.OrderStatusHistory.CountAsync());
        Assert.Equal(1, await db.OrderItems.CountAsync());
    }

    [Theory]
    [InlineData("number")]
    [InlineData("recipient")]
    [InlineData("phone")]
    [InlineData("location")]
    public async Task MissingRequiredRegistrationDataLeavesNoPartialRows(string field)
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var input = store.Input();
        input = field switch
        {
            "number" => input with { Number = " " },
            "recipient" => input with { RecipientName = " " },
            "phone" => input with { RecipientPhone = " " },
            _ => input with { StorageLocationId = int.MaxValue }
        };
        await Assert.ThrowsAsync<BusinessException>(() => store.Orders.RegisterAsync(input));
        await using var db = store.Factory.CreateDbContext();
        Assert.Empty(await db.Orders.ToListAsync());
        Assert.Empty(await db.OrderItems.ToListAsync());
        Assert.Empty(await db.OrderStatusHistory.ToListAsync());
    }
}
