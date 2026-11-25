using Microsoft.EntityFrameworkCore;
using Pvz.Core;

namespace Pvz.Tests;

public sealed class AdministrationTests
{
    [Fact]
    public async Task PasswordResetRejectsOldPasswordAndAcceptsNewCredentials()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.CreateOperatorAsync();
        const string replacement = "Replacement password 456!";
        await store.Admin.ResetPasswordAsync(id, replacement);
        var employee = (await store.Admin.GetEmployeesAsync()).Single(x => x.Id == id);
        Assert.Equal(EmployeeRole.Operator, employee.Role);
        Assert.True(employee.IsActive);
        await Assert.ThrowsAsync<BusinessException>(() => store.Access.LoginAsync("operator", TestStore.Password));
        Assert.Null(store.Session.CurrentUser);
        await store.Access.LoginAsync("operator", replacement);
        Assert.Equal(id, store.Session.CurrentUser!.Id);
    }

    [Fact]
    public async Task DeactivationPreservesHistoryAndDoesNotReleaseLogin()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.CreateOperatorAsync();
        await store.Access.LoginAsync("operator", TestStore.Password);
        var orderId = await store.RegisterAsync();
        await store.Access.LoginAsync("admin", TestStore.Password);
        await store.Admin.UpdateEmployeeAsync(id, new("Отключённый оператор", "operator", EmployeeRole.Operator, false));
        await Assert.ThrowsAsync<BusinessException>(() => store.Admin.CreateEmployeeAsync(
            new("Другой", " OPERATOR ", EmployeeRole.Operator, true), TestStore.Password));
        var history = Assert.Single((await store.Orders.GetAsync(orderId)).History);
        Assert.Equal("Отключённый оператор", history.EmployeeName);
        await using var db = store.Factory.CreateDbContext();
        Assert.Equal(id, (await db.OrderStatusHistory.SingleAsync()).EmployeeId);
        Assert.False((await db.Employees.SingleAsync(x => x.Id == id)).IsActive);
    }

    [Fact]
    public async Task FailedSelfUpdateLeavesSessionAndDatabaseUnchanged()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        await store.CreateOperatorAsync();
        var identity = store.Session.CurrentUser!;
        await Assert.ThrowsAsync<BusinessException>(() => store.Admin.UpdateEmployeeAsync(identity.Id,
            new("Не сохранённое имя", "operator", EmployeeRole.Administrator, true)));
        Assert.Equal(identity, store.Session.CurrentUser);
        var persisted = (await store.Admin.GetEmployeesAsync()).Single(x => x.Id == identity.Id);
        Assert.Equal(identity.Name, persisted.Name);
        Assert.Equal("Admin", persisted.Login);
        await store.Admin.UpdateEmployeeAsync(identity.Id, new("Новое имя", "owner", EmployeeRole.Administrator, true));
        Assert.Equal("Новое имя", store.Session.CurrentUser!.Name);
        await Assert.ThrowsAsync<BusinessException>(() => store.Access.LoginAsync("admin", TestStore.Password));
        await store.Access.LoginAsync(" OWNER ", TestStore.Password);
        Assert.Equal(identity.Id, store.Session.CurrentUser!.Id);
    }

    [Fact]
    public async Task SelfDeactivationEndsSessionAfterCommitWhenAnotherAdministratorExists()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        await store.Admin.CreateEmployeeAsync(new("Другой администратор", "second", EmployeeRole.Administrator, true), TestStore.Password);
        var id = store.Session.CurrentUser!.Id;
        await store.Admin.UpdateEmployeeAsync(id, new("Первый", "admin", EmployeeRole.Administrator, false));
        Assert.Null(store.Session.CurrentUser);
        await Assert.ThrowsAsync<BusinessException>(() => store.Access.LoginAsync("admin", TestStore.Password));
        await store.Access.LoginAsync("second", TestStore.Password);
        Assert.False((await store.Admin.GetEmployeesAsync()).Single(x => x.Id == id).IsActive);
    }

    [Fact]
    public async Task RenamedOccupiedLocationIsVisibleAndCanBeDeletedOnlyAfterIssue()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.RegisterAsync();
        await store.Admin.SaveLocationAsync(store.LocationId, " b ", " 02 ");
        Assert.Equal("B / 02", (await store.Orders.GetAsync(id)).Summary.Location);
        await Assert.ThrowsAsync<BusinessException>(() => store.Admin.DeleteLocationAsync(store.LocationId));
        await store.Orders.IssueAsync(id, true);
        await store.Admin.DeleteLocationAsync(store.LocationId);
        Assert.Empty(await store.Admin.GetLocationsAsync());
        var issued = await store.Orders.GetAsync(id);
        Assert.Equal(OrderStatus.Issued, issued.Summary.Status);
        Assert.Null(issued.StorageLocationId);
        Assert.Equal(2, issued.History.Count);
    }

    [Fact]
    public async Task DuplicateLocationRenameRollsBackAndMissingLocationIsRejected()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var other = await store.Admin.SaveLocationAsync(null, "B", "02");
        await Assert.ThrowsAsync<BusinessException>(() => store.Admin.SaveLocationAsync(other, " a ", " 01 "));
        Assert.Equal("B / 02", (await store.Admin.GetLocationsAsync()).Single(x => x.Id == other).Label);
        await Assert.ThrowsAsync<BusinessException>(() => store.Admin.SaveLocationAsync(int.MaxValue, "C", "03"));
        await Assert.ThrowsAsync<BusinessException>(() => store.Admin.DeleteLocationAsync(int.MaxValue));
        Assert.Equal(2, (await store.Admin.GetLocationsAsync()).Count);
    }
}
