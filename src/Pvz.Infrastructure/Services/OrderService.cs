using System.Data;
using Microsoft.EntityFrameworkCore;
using Pvz.Core;
using Pvz.Infrastructure.Data;

namespace Pvz.Infrastructure.Services;

public sealed class OrderService(PvzDbContextFactory factory, Session session, TimeProvider clock) : IOrderService
{
    public async Task<IReadOnlyList<OrderSummary>> SearchAsync(string? number = null, OrderStatus? status = null)
    {
        await using var db = factory.CreateDbContext();
        await ServiceRules.AuthorizeAsync(db, session);
        var query = db.Orders.AsNoTracking().Include(x => x.StorageLocation).AsQueryable();
        if (!string.IsNullOrWhiteSpace(number))
        {
            var normalized = ServiceRules.NormalizeNumber(number);
            query = query.Where(x => x.Number.Contains(normalized));
        }
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var orders = await query.OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.Id).ToListAsync();
        return orders.Select(Summary).ToArray();
    }

    public async Task<OrderDetails> GetAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        await ServiceRules.AuthorizeAsync(db, session);
        var order = await db.Orders.AsNoTracking().Include(x => x.StorageLocation).Include(x => x.Items)
            .Include(x => x.History).ThenInclude(x => x.Employee).AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Заказ не найден.");
        return new(Summary(order), order.StorageLocationId,
            order.Items.OrderBy(x => x.Id).Select(x => new ItemInput(x.Name, x.Quantity)).ToArray(),
            order.History.OrderBy(x => x.Id).Select(x => new HistoryInfo(x.Id, x.PreviousStatus,
                x.NewStatus, x.ChangedAt, x.Employee.Name, x.ReturnReason)).ToArray());
    }

    public async Task<int> RegisterAsync(RegisterOrderInput input)
    {
        var order = new Order
        {
            Number = ServiceRules.NormalizeNumber(input.Number), ReceivedAt = input.ReceivedAt,
            StorageUntil = input.StorageUntil, Status = OrderStatus.AwaitingPickup,
            StorageLocationId = input.StorageLocationId
        };
        ApplyEditable(order, input.RecipientName, input.RecipientPhone, input.Items);
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var employee = await ServiceRules.AuthorizeAsync(db, session);
        await CheckLocationAsync(db, input.StorageLocationId);
        var now = clock.GetUtcNow().UtcDateTime;
        if (input.ReceivedAt.Kind != DateTimeKind.Utc || input.StorageUntil.Kind != DateTimeKind.Utc
            || input.ReceivedAt > now || input.StorageUntil <= input.ReceivedAt)
            throw new BusinessException("Укажите даты в UTC: поступление не позже текущего момента, окончание хранения строго позже поступления.");
        order.History.Add(NewHistory(employee.Id, null, order.Status, now));
        db.Orders.Add(order);
        await ServiceRules.SaveAsync(db);
        await transaction.CommitAsync();
        return order.Id;
    }

    public async Task EditAsync(int id, EditOrderInput input)
    {
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ServiceRules.AuthorizeAsync(db, session);
        var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new BusinessException("Заказ не найден.");
        if (order.Status is not (OrderStatus.AwaitingPickup or OrderStatus.PendingReturn))
            throw new BusinessException("Редактирование доступно только до выдачи или при ожидании возврата.");
        await CheckLocationAsync(db, input.StorageLocationId);
        db.OrderItems.RemoveRange(order.Items);
        ApplyEditable(order, input.RecipientName, input.RecipientPhone, input.Items);
        order.StorageLocationId = input.StorageLocationId;
        await ServiceRules.SaveAsync(db);
        await transaction.CommitAsync();
    }

    public Task IssueAsync(int id, bool confirmed) => TransitionAsync(id, OrderStatus.Issued, null, null, confirmed);
    public Task StartReturnAsync(int id, string reason, int? locationId, bool confirmed) =>
        TransitionAsync(id, OrderStatus.PendingReturn, reason, locationId, confirmed);
    public Task CompleteReturnAsync(int id, bool confirmed) => TransitionAsync(id, OrderStatus.Returned, null, null, confirmed);

    private async Task TransitionAsync(int id, OrderStatus target, string? reason, int? locationId, bool confirmed)
    {
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var employee = await ServiceRules.AuthorizeAsync(db, session);
        if (!confirmed) return;
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new BusinessException("Заказ не найден.");
        var previous = order.Status;
        switch (target)
        {
            case OrderStatus.Issued when previous == OrderStatus.AwaitingPickup:
                order.StorageLocationId = null;
                break;
            case OrderStatus.PendingReturn when previous is OrderStatus.AwaitingPickup or OrderStatus.Issued:
                reason = ServiceRules.Required(reason, "Причина возврата", 500);
                if (previous == OrderStatus.Issued && !locationId.HasValue)
                    throw new BusinessException("После выдачи необходимо заново выбрать место хранения.");
                order.StorageLocationId = locationId ?? order.StorageLocationId;
                await CheckLocationAsync(db, order.StorageLocationId);
                break;
            case OrderStatus.Returned when previous == OrderStatus.PendingReturn:
                order.StorageLocationId = null;
                break;
            default:
                throw new BusinessException($"Операция недоступна для статуса «{previous.ToRussian()}».");
        }
        var now = clock.GetUtcNow().UtcDateTime;
        if (target == OrderStatus.Issued && now > order.StorageUntil)
            throw new BusinessException("Срок хранения истёк. Оформите заказ к возврату.");
        order.Status = target;
        order.History.Add(NewHistory(employee.Id, previous, target, now, reason));
        await ServiceRules.SaveAsync(db);
        await transaction.CommitAsync();
    }

    private OrderSummary Summary(Order order) => new(order.Id, order.Number, order.RecipientName,
        order.RecipientPhone, order.ReceivedAt, order.StorageUntil, order.Status,
        order.StorageLocation is { } location ? $"{location.Rack} / {location.Cell}" : "—",
        order.Status == OrderStatus.AwaitingPickup && clock.GetUtcNow().UtcDateTime > order.StorageUntil);

    private static void ApplyEditable(Order order, string name, string phone, IReadOnlyList<ItemInput> items)
    {
        order.RecipientName = ServiceRules.Required(name, "Получатель", 120);
        order.RecipientPhone = ServiceRules.Required(phone, "Телефон", 40);
        if (items is null || items.Count == 0) throw new BusinessException("Добавьте хотя бы одну позицию заказа.");
        order.Items = items.Select(item => new OrderItem
        {
            Name = ServiceRules.Required(item.Name, "Название позиции", 200),
            Quantity = item.Quantity > 0 ? item.Quantity : throw new BusinessException("Количество должно быть больше нуля.")
        }).ToList();
    }

    private static async Task CheckLocationAsync(PvzDbContext db, int? id)
    {
        if (!id.HasValue || !await db.StorageLocations.AnyAsync(x => x.Id == id))
            throw new BusinessException("Выберите существующее место хранения.");
    }

    private static OrderStatusHistory NewHistory(int employeeId, OrderStatus? previous,
        OrderStatus next, DateTime now, string? reason = null) => new()
    {
        EmployeeId = employeeId, PreviousStatus = previous, NewStatus = next, ChangedAt = now, ReturnReason = reason
    };
}
