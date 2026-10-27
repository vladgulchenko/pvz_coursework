using System.Collections.ObjectModel;
using System.Windows.Threading;
using Pvz.Core;
using Pvz.Wpf.Presentation;

namespace Pvz.Wpf.ViewModels;

public sealed record StatusFilter(string Label, OrderStatus? Status);

public sealed class MainViewModel : ScreenViewModel, IDisposable
{
    private readonly IOrderService orders;
    private readonly IAdministrationService administration;
    private readonly IDialogService dialogs;
    private readonly DispatcherTimer timer;
    private string search = "";
    private StatusFilter selectedFilter;
    private OrderSummary? selectedOrder;
    private OrderDetails? details;
    private LocationInfo? returnLocation;
    private string returnReason = "";
    private int selectionVersion;
    private bool disposed;

    public MainViewModel(IOrderService orders, IAdministrationService administration, IAccessService access,
        ISession session, IDialogService dialogs)
    {
        this.orders = orders;
        this.administration = administration;
        this.dialogs = dialogs;
        CurrentUser = session.CurrentUser!;
        selectedFilter = Filters[0];
        RefreshCommand = new(_ => RunAsync(RefreshAsync));
        NewOrderCommand = new(_ => RunAsync(async () =>
        {
            await LoadLocationsAsync();
            if (Locations.Count == 0) throw new BusinessException("Сначала администратор должен добавить место хранения.");
            if (dialogs.EditOrder(Locations.ToArray())) await RefreshAsync();
        }, RefreshAsync));
        EditOrderCommand = new(_ => RunAsync(async () =>
        {
            var id = Details?.Summary.Id ?? throw new BusinessException("Выберите заказ.");
            await LoadLocationsAsync();
            var current = await orders.GetAsync(id);
            if (dialogs.EditOrder(Locations.ToArray(), current)) await RefreshAsync(id);
        }, RefreshAsync), () => CanEdit);
        IssueCommand = new(_ => RunAsync(async () =>
        {
            var order = Details?.Summary ?? throw new BusinessException("Выберите заказ.");
            if (!dialogs.Confirm("Выдача заказа", $"Подтвердить выдачу всего заказа № {order.Number}?")) return;
            await orders.IssueAsync(order.Id, true);
            await RefreshAsync(order.Id);
        }, RefreshAsync), () => CanIssue);
        StartReturnCommand = new(_ => RunAsync(async () =>
        {
            var order = Details?.Summary ?? throw new BusinessException("Выберите заказ.");
            if (string.IsNullOrWhiteSpace(ReturnReason)) throw new BusinessException("Укажите причину возврата.");
            if (!dialogs.Confirm("Приём к возврату", $"Весь заказ № {order.Number} находится в ПВЗ и принимается к возврату?")) return;
            await orders.StartReturnAsync(order.Id, ReturnReason, ReturnLocation?.Id, true);
            await RefreshAsync(order.Id);
        }, RefreshAsync), () => CanStartReturn);
        CompleteReturnCommand = new(_ => RunAsync(async () =>
        {
            var order = Details?.Summary ?? throw new BusinessException("Выберите заказ.");
            if (!dialogs.Confirm("Завершение возврата", $"Заказ № {order.Number} передан на обратную отправку?")) return;
            await orders.CompleteReturnAsync(order.Id, true);
            await RefreshAsync(order.Id);
        }, RefreshAsync), () => CanCompleteReturn);
        EmployeesCommand = new(_ => RunAsync(async () => { dialogs.ShowEmployees(); await RefreshAsync(); }), () => IsAdministrator);
        LocationsCommand = new(_ => RunAsync(async () => { dialogs.ShowLocations(); await RefreshAsync(); }), () => IsAdministrator);
        LogoutCommand = new(access.Logout);
        timer = new(TimeSpan.FromSeconds(30), DispatcherPriority.Background, OnTimer, Dispatcher.CurrentDispatcher);
        timer.Start();
    }

    public UserIdentity CurrentUser { get; }
    public bool IsAdministrator => CurrentUser.Role == EmployeeRole.Administrator;
    public ObservableCollection<OrderSummary> Orders { get; } = [];
    public ObservableCollection<LocationInfo> Locations { get; } = [];
    public IReadOnlyList<StatusFilter> Filters { get; } = new[]
    {
        new StatusFilter("Все статусы", null), new("Ожидает выдачи", OrderStatus.AwaitingPickup),
        new("Выдан", OrderStatus.Issued), new("Ожидает возврата", OrderStatus.PendingReturn), new("Возвращён", OrderStatus.Returned)
    };
    public string Search { get => search; set => Set(ref search, value); }
    public StatusFilter SelectedFilter { get => selectedFilter; set => Set(ref selectedFilter, value); }
    public OrderSummary? SelectedOrder
    {
        get => selectedOrder;
        set
        {
            if (!Set(ref selectedOrder, value)) return;
            var version = ++selectionVersion;
            Details = null;
            _ = SelectAsync(value?.Id, version);
        }
    }
    public OrderDetails? Details
    {
        get => details;
        private set
        {
            Set(ref details, value);
            OnPropertyChanged(nameof(HasDetails));
            OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanIssue));
            OnPropertyChanged(nameof(CanStartReturn)); OnPropertyChanged(nameof(CanCompleteReturn));
            EditOrderCommand?.Refresh(); IssueCommand?.Refresh(); StartReturnCommand?.Refresh(); CompleteReturnCommand?.Refresh();
        }
    }
    public bool HasDetails => Details is not null;
    public bool CanEdit => Details?.Summary.Status is OrderStatus.AwaitingPickup or OrderStatus.PendingReturn;
    public bool CanIssue => Details?.Summary is { Status: OrderStatus.AwaitingPickup, IsOverdue: false };
    public bool CanStartReturn => Details?.Summary.Status is OrderStatus.AwaitingPickup or OrderStatus.Issued;
    public bool CanCompleteReturn => Details?.Summary.Status == OrderStatus.PendingReturn;
    public string ReturnReason { get => returnReason; set => Set(ref returnReason, value); }
    public LocationInfo? ReturnLocation { get => returnLocation; set => Set(ref returnLocation, value); }
    public int TotalCount => Orders.Count;
    public int AwaitingCount => Orders.Count(x => x.Status == OrderStatus.AwaitingPickup);
    public int OverdueCount => Orders.Count(x => x.IsOverdue);
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand NewOrderCommand { get; }
    public AsyncCommand EditOrderCommand { get; }
    public AsyncCommand IssueCommand { get; }
    public AsyncCommand StartReturnCommand { get; }
    public AsyncCommand CompleteReturnCommand { get; }
    public AsyncCommand EmployeesCommand { get; }
    public AsyncCommand LocationsCommand { get; }
    public RelayCommand LogoutCommand { get; }

    public Task InitializeAsync() => RunAsync(() => RefreshAsync());

    private async Task SelectAsync(int? id, int version)
    {
        if (!id.HasValue || disposed) return;
        await RunAsync(async () =>
        {
            var result = await orders.GetAsync(id.Value);
            if (version == selectionVersion && !disposed) ApplyDetails(result);
        });
    }

    private async Task RefreshAsync() => await RefreshAsync(SelectedOrder?.Id);

    private async Task RefreshAsync(int? selectedId)
    {
        if (disposed) return;
        var rows = await orders.SearchAsync(Search, SelectedFilter.Status);
        await LoadLocationsAsync();
        if (disposed) return;
        Orders.Clear();
        foreach (var row in rows) Orders.Add(row);
        // Assign directly: the refresh already loads the selected card itself.
        selectedOrder = rows.FirstOrDefault(x => x.Id == selectedId);
        OnPropertyChanged(nameof(SelectedOrder));
        ++selectionVersion;
        Details = null;
        if (selectedOrder is not null) ApplyDetails(await orders.GetAsync(selectedOrder.Id));
        OnPropertyChanged(nameof(TotalCount)); OnPropertyChanged(nameof(AwaitingCount)); OnPropertyChanged(nameof(OverdueCount));
    }

    private async Task LoadLocationsAsync()
    {
        var locations = await administration.GetLocationsAsync();
        if (disposed) return;
        Locations.Clear();
        foreach (var location in locations) Locations.Add(location);
    }

    private void ApplyDetails(OrderDetails result)
    {
        Details = result;
        ReturnLocation = Locations.FirstOrDefault(x => x.Id == result.StorageLocationId);
        ReturnReason = result.Summary.IsOverdue ? "Истек срок хранения" : "";
    }

    private async void OnTimer(object? sender, EventArgs e)
    {
        if (!IsBusy && !disposed) await RunAsync(RefreshAsync);
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        timer.Tick -= OnTimer;
        Orders.Clear(); Locations.Clear(); Details = null;
    }
}
