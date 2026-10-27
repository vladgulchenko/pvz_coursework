using Microsoft.EntityFrameworkCore;
using Pvz.Core;
using Pvz.Infrastructure.Data;
using Pvz.Infrastructure.Services;

namespace Pvz.Tests;

public sealed class TestClock : TimeProvider
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    public override DateTimeOffset GetUtcNow() => new(UtcNow);
}

public sealed class TestStore : IAsyncDisposable
{
    public const string Password = "Test-only Pass 123!";
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "Pvz.Tests", Guid.NewGuid().ToString("N"));
    public string FilePath => Path.Combine(Folder, "test.db");
    public PvzDbContextFactory Factory { get; }
    public TestClock Clock { get; } = new();
    public Session Session { get; } = new();
    public AccessService Access { get; }
    public AdministrationService Admin { get; }
    public OrderService Orders { get; }
    public int LocationId { get; private set; }

    public TestStore()
    {
        Directory.CreateDirectory(Folder);
        Factory = PvzDbContextFactory.ForFile(FilePath);
        Access = new(Factory, Session);
        Admin = new(Factory, Session);
        Orders = new(Factory, Session, Clock);
    }

    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(Factory, FilePath).InitializeAsync();
        await Access.CreateInitialAdministratorAsync("Тестовый администратор", "Admin", Password);
        await Access.LoginAsync(" admin ", Password);
        LocationId = await Admin.SaveLocationAsync(null, " a ", " 01 ");
    }

    public RegisterOrderInput Input(string number = " test-001 ") => new(number, "Вымышленный получатель",
        "+375000000000", Clock.UtcNow.AddHours(-1), Clock.UtcNow.AddDays(3), LocationId, [new("Тестовая книга", 2)]);

    public Task<int> RegisterAsync(string number = " test-001 ") => Orders.RegisterAsync(Input(number));

    public async Task<int> CreateOperatorAsync() => await Admin.CreateEmployeeAsync(
        new("Тестовый оператор", "operator", EmployeeRole.Operator, true), Password);

    public ValueTask DisposeAsync()
    {
        Directory.Delete(Folder, recursive: true);
        return ValueTask.CompletedTask;
    }
}
