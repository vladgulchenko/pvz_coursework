using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pvz.Core;
using Pvz.Infrastructure.Data;

namespace Pvz.Infrastructure.Services;

public sealed class AccessService(PvzDbContextFactory factory, Session session) : IAccessService
{
    internal static PasswordHasher<Employee> CreateHasher() => new(Options.Create(
        new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = 210_000 }));

    public async Task<bool> NeedsSetupAsync()
    {
        await using var db = factory.CreateDbContext();
        if (await db.Employees.AnyAsync(x => x.IsActive && x.Role == EmployeeRole.Administrator)) return false;
        if (await HasDataAsync(db))
            throw new BusinessException("В базе отсутствует активный администратор. Требуется восстановление из резервной копии; первоначальная настройка заблокирована.");
        return true;
    }

    public async Task CreateInitialAdministratorAsync(string name, string login, string password)
    {
        ServiceRules.Password(password);
        var employee = new Employee
        {
            Name = ServiceRules.Required(name, "Имя", 120), Login = ServiceRules.Required(login, "Логин", 80),
            NormalizedLogin = ServiceRules.NormalizeLogin(login), Role = EmployeeRole.Administrator
        };
        employee.PasswordHash = CreateHasher().HashPassword(employee, password);
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await HasDataAsync(db)) throw new BusinessException("Первоначальная настройка уже выполнена или база содержит данные.");
        db.Employees.Add(employee);
        await ServiceRules.SaveAsync(db);
        await transaction.CommitAsync();
    }

    public async Task LoginAsync(string login, string password)
    {
        session.Clear();
        const string failure = "Неверный логин или пароль";
        if (string.IsNullOrWhiteSpace(login) || login.Trim().Length > 80 || string.IsNullOrEmpty(password) || password.Length > 128)
            throw new BusinessException(failure);
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var normalized = ServiceRules.NormalizeLogin(login);
        var employee = await db.Employees.SingleOrDefaultAsync(x => x.NormalizedLogin == normalized);
        if (employee is null || !employee.IsActive) throw new BusinessException(failure);
        PasswordVerificationResult result;
        try { result = CreateHasher().VerifyHashedPassword(employee, employee.PasswordHash, password); }
        catch (FormatException) { throw new BusinessException(failure); }
        if (result == PasswordVerificationResult.Failed) throw new BusinessException(failure);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            employee.PasswordHash = CreateHasher().HashPassword(employee, password);
            await ServiceRules.SaveAsync(db);
        }
        await transaction.CommitAsync();
        session.Start(employee);
    }

    public void Logout() => session.Clear();

    private static async Task<bool> HasDataAsync(PvzDbContext db) =>
        await db.Employees.AnyAsync() || await db.Orders.AnyAsync() || await db.StorageLocations.AnyAsync()
        || await db.OrderItems.AnyAsync() || await db.OrderStatusHistory.AnyAsync();
}
