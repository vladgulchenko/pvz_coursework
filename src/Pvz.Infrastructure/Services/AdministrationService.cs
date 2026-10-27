using System.Data;
using Microsoft.EntityFrameworkCore;
using Pvz.Core;
using Pvz.Infrastructure.Data;

namespace Pvz.Infrastructure.Services;

public sealed class AdministrationService(PvzDbContextFactory factory, Session session) : IAdministrationService
{
    public async Task<IReadOnlyList<EmployeeInfo>> GetEmployeesAsync()
    {
        await using var db = factory.CreateDbContext();
        await ServiceRules.AuthorizeAsync(db, session, admin: true);
        return await db.Employees.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new EmployeeInfo(x.Id, x.Name, x.Login, x.Role, x.IsActive)).ToListAsync();
    }

    public async Task<int> CreateEmployeeAsync(EmployeeInput input, string password)
    {
        ServiceRules.Password(password);
        var employee = new Employee();
        Apply(employee, input);
        employee.PasswordHash = AccessService.CreateHasher().HashPassword(employee, password);
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ServiceRules.AuthorizeAsync(db, session, admin: true);
        db.Employees.Add(employee);
        await ServiceRules.SaveAsync(db);
        await transaction.CommitAsync();
        return employee.Id;
    }

    public async Task UpdateEmployeeAsync(int id, EmployeeInput input)
    {
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var actor = await ServiceRules.AuthorizeAsync(db, session, admin: true);
        var employee = await db.Employees.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new BusinessException("Сотрудник не найден.");
        if (employee.IsActive && employee.Role == EmployeeRole.Administrator
            && (!input.IsActive || input.Role != EmployeeRole.Administrator)
            && await db.Employees.CountAsync(x => x.IsActive && x.Role == EmployeeRole.Administrator) <= 1)
            throw new BusinessException("Нельзя отключить или понизить последнего активного администратора.");
        var endSession = actor.Id == id && (employee.IsActive != input.IsActive || employee.Role != input.Role);
        Apply(employee, input);
        await ServiceRules.SaveAsync(db);
        await transaction.CommitAsync();
        if (endSession) session.Clear();
        else if (actor.Id == id) session.Start(employee);
    }

    public async Task ResetPasswordAsync(int id, string password)
    {
        ServiceRules.Password(password);
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ServiceRules.AuthorizeAsync(db, session, admin: true);
        var employee = await db.Employees.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new BusinessException("Сотрудник не найден.");
        employee.PasswordHash = AccessService.CreateHasher().HashPassword(employee, password);
        await ServiceRules.SaveAsync(db);
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<LocationInfo>> GetLocationsAsync()
    {
        await using var db = factory.CreateDbContext();
        await ServiceRules.AuthorizeAsync(db, session);
        return await db.StorageLocations.AsNoTracking().OrderBy(x => x.Rack).ThenBy(x => x.Cell)
            .Select(x => new LocationInfo(x.Id, x.Rack, x.Cell)).ToListAsync();
    }

    public async Task<int> SaveLocationAsync(int? id, string rack, string cell)
    {
        var normalizedRack = ServiceRules.Required(rack, "Стеллаж", 40).ToUpperInvariant();
        var normalizedCell = ServiceRules.Required(cell, "Ячейка", 40).ToUpperInvariant();
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ServiceRules.AuthorizeAsync(db, session, admin: true);
        var location = id.HasValue
            ? await db.StorageLocations.SingleOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Место хранения не найдено.")
            : new StorageLocation();
        location.Rack = normalizedRack;
        location.Cell = normalizedCell;
        if (!id.HasValue) db.StorageLocations.Add(location);
        await ServiceRules.SaveAsync(db);
        await transaction.CommitAsync();
        return location.Id;
    }

    public async Task DeleteLocationAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ServiceRules.AuthorizeAsync(db, session, admin: true);
        if (await db.Orders.AnyAsync(x => x.StorageLocationId == id))
            throw new BusinessException("Место занято заказами. Сначала измените их размещение.");
        var location = await db.StorageLocations.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new BusinessException("Место хранения не найдено.");
        db.StorageLocations.Remove(location);
        await ServiceRules.SaveAsync(db);
        await transaction.CommitAsync();
    }

    private static void Apply(Employee employee, EmployeeInput input)
    {
        if (!Enum.IsDefined(input.Role)) throw new BusinessException("Неизвестная роль сотрудника.");
        employee.Name = ServiceRules.Required(input.Name, "Имя", 120);
        employee.Login = ServiceRules.Required(input.Login, "Логин", 80);
        employee.NormalizedLogin = ServiceRules.NormalizeLogin(input.Login);
        employee.Role = input.Role;
        employee.IsActive = input.IsActive;
    }
}
