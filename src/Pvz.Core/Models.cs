namespace Pvz.Core;

public enum EmployeeRole { Operator, Administrator }
public enum OrderStatus { AwaitingPickup, Issued, PendingReturn, Returned }

public sealed class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Login { get; set; } = "";
    public string NormalizedLogin { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public EmployeeRole Role { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class StorageLocation
{
    public int Id { get; set; }
    public string Rack { get; set; } = "";
    public string Cell { get; set; } = "";
}

public sealed class Order
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string RecipientName { get; set; } = "";
    public string RecipientPhone { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime StorageUntil { get; set; }
    public OrderStatus Status { get; set; }
    public int? StorageLocationId { get; set; }
    public StorageLocation? StorageLocation { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public List<OrderStatusHistory> History { get; set; } = [];
}

public sealed class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class OrderStatusHistory
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public OrderStatus? PreviousStatus { get; set; }
    public OrderStatus NewStatus { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ReturnReason { get; set; }
}
