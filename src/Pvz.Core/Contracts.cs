namespace Pvz.Core;

public sealed record UserIdentity(int Id, string Name, EmployeeRole Role)
{
    public string RoleText => Role.ToRussian();
}

public interface ISession
{
    UserIdentity? CurrentUser { get; }
    event EventHandler? Changed;
}

public sealed record EmployeeInfo(int Id, string Name, string Login, EmployeeRole Role, bool IsActive)
{
    public string RoleText => Role.ToRussian();
    public string ActivityText => IsActive ? "Активен" : "Отключён";
}

public sealed record LocationInfo(int Id, string Rack, string Cell)
{
    public string Label => $"{Rack} / {Cell}";
}

public sealed record ItemInput(string Name, int Quantity);
public sealed record RegisterOrderInput(string Number, string RecipientName, string RecipientPhone,
    DateTime ReceivedAt, DateTime StorageUntil, int StorageLocationId, IReadOnlyList<ItemInput> Items);
public sealed record EditOrderInput(string RecipientName, string RecipientPhone,
    int StorageLocationId, IReadOnlyList<ItemInput> Items);
public sealed record EmployeeInput(string Name, string Login, EmployeeRole Role, bool IsActive);
public sealed record HistoryInfo(int Id, OrderStatus? PreviousStatus, OrderStatus NewStatus,
    DateTime ChangedAt, string EmployeeName, string? ReturnReason)
{
    public string Transition => $"{(PreviousStatus.HasValue ? PreviousStatus.Value.ToRussian() : "Регистрация")} → {NewStatus.ToRussian()}";
    public DateTime ChangedAtLocal => ChangedAt.ToLocalTime();
}

public sealed record OrderSummary(int Id, string Number, string RecipientName, string RecipientPhone,
    DateTime ReceivedAt, DateTime StorageUntil, OrderStatus Status, string Location, bool IsOverdue)
{
    public string StatusText => Status.ToRussian();
    public string OverdueText => IsOverdue ? "Срок истёк" : "";
    public DateTime ReceivedAtLocal => ReceivedAt.ToLocalTime();
    public DateTime StorageUntilLocal => StorageUntil.ToLocalTime();
}

public sealed record OrderDetails(OrderSummary Summary, int? StorageLocationId,
    IReadOnlyList<ItemInput> Items, IReadOnlyList<HistoryInfo> History);

public interface IAccessService
{
    Task<bool> NeedsSetupAsync();
    Task CreateInitialAdministratorAsync(string name, string login, string password);
    Task LoginAsync(string login, string password);
    void Logout();
}

public interface IAdministrationService
{
    Task<IReadOnlyList<EmployeeInfo>> GetEmployeesAsync();
    Task<int> CreateEmployeeAsync(EmployeeInput input, string password);
    Task UpdateEmployeeAsync(int id, EmployeeInput input);
    Task ResetPasswordAsync(int id, string password);
    Task<IReadOnlyList<LocationInfo>> GetLocationsAsync();
    Task<int> SaveLocationAsync(int? id, string rack, string cell);
    Task DeleteLocationAsync(int id);
}

public interface IOrderService
{
    Task<IReadOnlyList<OrderSummary>> SearchAsync(string? number = null, OrderStatus? status = null);
    Task<OrderDetails> GetAsync(int id);
    Task<int> RegisterAsync(RegisterOrderInput input);
    Task EditAsync(int id, EditOrderInput input);
    Task IssueAsync(int id, bool confirmed);
    Task StartReturnAsync(int id, string reason, int? locationId, bool confirmed);
    Task CompleteReturnAsync(int id, bool confirmed);
}

public class BusinessException(string message) : Exception(message);
public sealed class AccessDeniedException(string message) : BusinessException(message);

public static class DisplayNames
{
    public static string ToRussian(this EmployeeRole role) => role switch
    {
        EmployeeRole.Operator => "Оператор",
        EmployeeRole.Administrator => "Администратор",
        _ => "Неизвестная роль"
    };

    public static string ToRussian(this OrderStatus status) => status switch
    {
        OrderStatus.AwaitingPickup => "Ожидает выдачи",
        OrderStatus.Issued => "Выдан",
        OrderStatus.PendingReturn => "Ожидает возврата",
        OrderStatus.Returned => "Возвращён",
        _ => "Неизвестный статус"
    };
}
