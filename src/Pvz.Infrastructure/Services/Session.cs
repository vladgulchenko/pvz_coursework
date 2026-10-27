using Pvz.Core;

namespace Pvz.Infrastructure.Services;

public sealed class Session : ISession
{
    public UserIdentity? CurrentUser { get; private set; }
    public event EventHandler? Changed;

    internal void Start(Employee employee)
    {
        CurrentUser = new(employee.Id, employee.Name, employee.Role);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Clear()
    {
        if (CurrentUser is null) return;
        CurrentUser = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
