using System.Collections.ObjectModel;
using Pvz.Core;
using Pvz.Wpf.Presentation;

namespace Pvz.Wpf.ViewModels;

public sealed record RoleOption(string Label, EmployeeRole Role);

public sealed class EmployeesViewModel : ScreenViewModel
{
    private readonly IAdministrationService administration;
    private readonly ISession session;
    private EmployeeInfo? selected;
    private string name = "";
    private string login = "";
    private RoleOption role;
    private bool active = true;

    public EmployeesViewModel(IAdministrationService administration, ISession session)
    {
        this.administration = administration;
        this.session = session;
        role = Roles[0];
        NewCommand = new(() => { Selected = null; ClearEditor(); });
        SaveCommand = new(parameter => RunAsync(async () =>
        {
            var input = new EmployeeInput(Name, Login, Role.Role, Active);
            if (Selected is null) await administration.CreateEmployeeAsync(input, parameter as string ?? "");
            else await administration.UpdateEmployeeAsync(Selected.Id, input);
            if (session.CurrentUser is null) return;
            await ReloadAsync();
            Message = "Данные сотрудника сохранены.";
        }, ReloadAsync));
        ResetPasswordCommand = new(parameter => RunAsync(async () =>
        {
            var id = Selected?.Id ?? throw new BusinessException("Выберите сотрудника.");
            await administration.ResetPasswordAsync(id, parameter as string ?? "");
            Message = "Новый пароль сохранён.";
        }, ReloadAsync));
    }

    public ObservableCollection<EmployeeInfo> Employees { get; } = [];
    public IReadOnlyList<RoleOption> Roles { get; } = new[] { new RoleOption("Оператор", EmployeeRole.Operator), new("Администратор", EmployeeRole.Administrator) };
    public EmployeeInfo? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            if (value is null) ClearEditor();
            else
            {
                Name = value.Name; Login = value.Login; Active = value.IsActive;
                Role = Roles.Single(x => x.Role == value.Role);
            }
            OnPropertyChanged(nameof(EditorTitle));
        }
    }
    public string EditorTitle => Selected is null ? "Новый сотрудник" : "Учётная запись";
    public string Name { get => name; set => Set(ref name, value); }
    public string Login { get => login; set => Set(ref login, value); }
    public RoleOption Role { get => role; set => Set(ref role, value); }
    public bool Active { get => active; set => Set(ref active, value); }
    public RelayCommand NewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ResetPasswordCommand { get; }

    public Task InitializeAsync() => RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        if (session.CurrentUser is null) return;
        var id = Selected?.Id;
        var rows = await administration.GetEmployeesAsync();
        Employees.Clear();
        foreach (var employee in rows) Employees.Add(employee);
        Selected = rows.FirstOrDefault(x => x.Id == id);
    }

    private void ClearEditor() { Name = ""; Login = ""; Role = Roles[0]; Active = true; }
}
