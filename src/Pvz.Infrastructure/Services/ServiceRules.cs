using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pvz.Core;
using Pvz.Infrastructure.Data;

namespace Pvz.Infrastructure.Services;

internal static class ServiceRules
{
    internal static string Required(string? value, string field, int max)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Length > max)
            throw new BusinessException($"{field}: укажите от 1 до {max} символов.");
        return text;
    }

    internal static string NormalizeLogin(string login) => Required(login, "Логин", 80).ToUpperInvariant();
    internal static string NormalizeNumber(string number) => new(Required(number, "Номер заказа", 80)
        .Select(c => c is >= 'a' and <= 'z' ? (char)(c - 'a' + 'A') : c).ToArray());

    internal static void Password(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8 || password.Length > 128)
            throw new BusinessException("Пароль должен содержать от 8 до 128 символов и не состоять из пробелов.");
    }

    internal static async Task<Employee> AuthorizeAsync(PvzDbContext db, Session session, bool admin = false)
    {
        var identity = session.CurrentUser;
        if (identity is null) throw new AccessDeniedException("Войдите в систему.");
        var employee = await db.Employees.SingleOrDefaultAsync(x => x.Id == identity.Id);
        if (employee is null || !employee.IsActive || employee.Role != identity.Role)
        {
            session.Clear();
            throw new AccessDeniedException("Сеанс завершён. Войдите в систему снова.");
        }
        if (admin && employee.Role != EmployeeRole.Administrator)
            throw new AccessDeniedException("Эта операция доступна только администратору.");
        return employee;
    }

    internal static async Task SaveAsync(PvzDbContext db)
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            throw new BusinessException("Заказ уже изменён. Обновите данные и повторите действие.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new BusinessException("Не удалось сохранить: запись дублируется или нарушает ограничения данных. Обновите список и проверьте ввод.");
        }
    }
}
