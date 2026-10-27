using Microsoft.EntityFrameworkCore;
using Pvz.Core;
using Pvz.Infrastructure.Data;
using Pvz.Infrastructure.Services;

namespace Pvz.Tests;

public sealed class AccessTests
{
    [Fact]
    public async Task InitialSetupAndMigrationAreRepeatableAndNeverCreateDefaultPassword()
    {
        await using var store = new TestStore();
        var initializer = new DatabaseInitializer(store.Factory, store.FilePath);
        await initializer.InitializeAsync();
        Assert.True(await store.Access.NeedsSetupAsync());
        await store.Access.CreateInitialAdministratorAsync("Администратор", "owner", TestStore.Password);
        Assert.False(await store.Access.NeedsSetupAsync());
        await initializer.InitializeAsync();
        await using var db = store.Factory.CreateDbContext();
        var employee = await db.Employees.SingleAsync();
        Assert.NotEqual(TestStore.Password, employee.PasswordHash);
        Assert.Null(store.Session.CurrentUser);
        await Assert.ThrowsAsync<BusinessException>(() => store.Access.CreateInitialAdministratorAsync("Другой", "other", TestStore.Password));
        Assert.Single(await db.Employees.ToListAsync());
    }

    [Fact]
    public async Task CorruptedNonemptyDatabaseCannotBeTakenOverThroughSetup()
    {
        await using var store = new TestStore();
        await new DatabaseInitializer(store.Factory, store.FilePath).InitializeAsync();
        await using (var db = store.Factory.CreateDbContext())
        {
            db.StorageLocations.Add(new() { Rack = "A", Cell = "01" });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<BusinessException>(() => store.Access.NeedsSetupAsync());
        await Assert.ThrowsAsync<BusinessException>(() => store.Access.CreateInitialAdministratorAsync("Имя", "login", TestStore.Password));
    }

    [Fact]
    public async Task WrongPasswordAndInactiveAccountHaveSameFailureAndNoSession()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var id = await store.CreateOperatorAsync();
        var wrong = await Assert.ThrowsAsync<BusinessException>(() => store.Access.LoginAsync("operator", "wrong"));
        Assert.Null(store.Session.CurrentUser);
        await store.Access.LoginAsync("admin", TestStore.Password);
        await store.Admin.UpdateEmployeeAsync(id, new("Имя", "operator", EmployeeRole.Operator, false));
        var inactive = await Assert.ThrowsAsync<BusinessException>(() => store.Access.LoginAsync("operator", TestStore.Password));
        var unknown = await Assert.ThrowsAsync<BusinessException>(() => store.Access.LoginAsync("absent", TestStore.Password));
        Assert.Equal("Неверный логин или пароль", wrong.Message);
        Assert.Equal(wrong.Message, inactive.Message);
        Assert.Equal(wrong.Message, unknown.Message);
        Assert.Null(store.Session.CurrentUser);
    }

    [Fact]
    public async Task OperatorCannotCallAdministrativeServicesDirectly()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        await store.CreateOperatorAsync();
        await store.Access.LoginAsync("operator", TestStore.Password);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.Admin.GetEmployeesAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.Admin.SaveLocationAsync(null, "B", "2"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.Admin.DeleteLocationAsync(store.LocationId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.Admin.CreateEmployeeAsync(new("Имя", "new", EmployeeRole.Administrator, true), TestStore.Password));
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.Admin.UpdateEmployeeAsync(1, new("Имя", "admin", EmployeeRole.Operator, true)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.Admin.ResetPasswordAsync(1, TestStore.Password));
        Assert.Single(await store.Admin.GetLocationsAsync());
        await store.RegisterAsync();
    }

    [Theory]
    [InlineData(EmployeeRole.Operator, true)]
    [InlineData(EmployeeRole.Administrator, false)]
    public async Task LastActiveAdministratorCannotBeDemotedOrDeactivated(EmployeeRole role, bool active)
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        await Assert.ThrowsAsync<BusinessException>(() => store.Admin.UpdateEmployeeAsync(1, new("Админ", "admin", role, active)));
        var admin = Assert.Single(await store.Admin.GetEmployeesAsync());
        Assert.True(admin.IsActive);
        Assert.Equal(EmployeeRole.Administrator, admin.Role);
    }

    [Fact]
    public async Task ChangingOwnRoleEndsSessionOnlyAfterSuccessfulSave()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        await store.Admin.CreateEmployeeAsync(new("Второй", "second", EmployeeRole.Administrator, true), TestStore.Password);
        await store.Admin.UpdateEmployeeAsync(1, new("Первый", "admin", EmployeeRole.Operator, true));
        Assert.Null(store.Session.CurrentUser);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.Orders.SearchAsync());
        await store.Access.LoginAsync("admin", TestStore.Password);
        Assert.Equal(EmployeeRole.Operator, store.Session.CurrentUser!.Role);
        store.Access.Logout();
        Assert.Null(store.Session.CurrentUser);
    }

    [Fact]
    public async Task ActiveStatusAndRoleAreRecheckedAgainstDatabase()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var operatorId = await store.CreateOperatorAsync();
        var otherSession = new Session();
        var otherAccess = new AccessService(store.Factory, otherSession);
        var otherOrders = new OrderService(store.Factory, otherSession, store.Clock);
        await otherAccess.LoginAsync("operator", TestStore.Password);
        await store.Admin.UpdateEmployeeAsync(operatorId, new("Оператор", "operator", EmployeeRole.Operator, false));
        await Assert.ThrowsAsync<AccessDeniedException>(() => otherOrders.SearchAsync());
        Assert.Null(otherSession.CurrentUser);
    }

    [Fact]
    public async Task LoginIsNormalizedAndPasswordWhitespaceIsPreserved()
    {
        await using var store = new TestStore();
        await store.InitializeAsync();
        var password = "  Password 123  ";
        await store.Admin.CreateEmployeeAsync(new("Имя", " login ", EmployeeRole.Operator, true), password);
        await Assert.ThrowsAsync<BusinessException>(() => store.Admin.CreateEmployeeAsync(new("Имя", "LOGIN", EmployeeRole.Operator, true), password));
        await store.Access.LoginAsync(" LoGiN ", password);
        await Assert.ThrowsAsync<BusinessException>(() => store.Access.LoginAsync("login", password.Trim()));
    }
}
