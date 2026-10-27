using System.Collections.ObjectModel;
using System.Globalization;
using Pvz.Core;
using Pvz.Wpf.Presentation;

namespace Pvz.Wpf.ViewModels;

public sealed class ItemRow : ObservableObject
{
    private string name = "";
    private string quantity = "1";
    public string Name { get => name; set => Set(ref name, value); }
    public string Quantity { get => quantity; set => Set(ref quantity, value); }
}

public sealed class OrderEditorViewModel : ScreenViewModel
{
    private string number = "";
    private string recipientName = "";
    private string phone = "";
    private string receivedAt = "";
    private string storageUntil = "";
    private LocationInfo? location;
    private ItemRow? selectedItem;
    public event EventHandler? Saved;

    public OrderEditorViewModel(IOrderService orders, IReadOnlyList<LocationInfo> locations, OrderDetails? existing)
    {
        IsNew = existing is null;
        Locations = locations;
        Number = existing?.Summary.Number ?? "";
        RecipientName = existing?.Summary.RecipientName ?? "";
        Phone = existing?.Summary.RecipientPhone ?? "";
        ReceivedAt = (existing?.Summary.ReceivedAtLocal ?? DateTime.Now.AddMinutes(-1)).ToString("dd.MM.yyyy HH:mm");
        StorageUntil = (existing?.Summary.StorageUntilLocal ?? DateTime.Now.AddDays(7)).ToString("dd.MM.yyyy HH:mm");
        Location = locations.FirstOrDefault(x => x.Id == existing?.StorageLocationId);
        foreach (var item in existing?.Items ?? [new ItemInput("", 1)])
            Items.Add(new() { Name = item.Name, Quantity = item.Quantity.ToString(CultureInfo.InvariantCulture) });
        AddItemCommand = new(() => Items.Add(new()));
        RemoveItemCommand = new(() => { if (SelectedItem is not null) Items.Remove(SelectedItem); });
        SaveCommand = new(_ => RunAsync(async () =>
        {
            if (Location is null) throw new BusinessException("Выберите место хранения.");
            var items = Items.Select(x => new ItemInput(x.Name,
                int.TryParse(x.Quantity, out var count) ? count : throw new BusinessException("Количество должно быть целым положительным числом."))).ToArray();
            if (existing is null)
            {
                await orders.RegisterAsync(new(Number, RecipientName, Phone, ParseDate(ReceivedAt),
                    ParseDate(StorageUntil), Location.Id, items));
            }
            else await orders.EditAsync(existing.Summary.Id, new(RecipientName, Phone, Location.Id, items));
            Saved?.Invoke(this, EventArgs.Empty);
        }, async () =>
        {
            if (existing is null) return;
            var current = await orders.GetAsync(existing.Summary.Id);
            RecipientName = current.Summary.RecipientName;
            Phone = current.Summary.RecipientPhone;
            Location = Locations.FirstOrDefault(x => x.Id == current.StorageLocationId);
            Items.Clear();
            foreach (var item in current.Items)
                Items.Add(new() { Name = item.Name, Quantity = item.Quantity.ToString(CultureInfo.InvariantCulture) });
        }));
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "Регистрация заказа" : $"Редактирование № {Number}";
    public string Number { get => number; set => Set(ref number, value); }
    public string RecipientName { get => recipientName; set => Set(ref recipientName, value); }
    public string Phone { get => phone; set => Set(ref phone, value); }
    public string ReceivedAt { get => receivedAt; set => Set(ref receivedAt, value); }
    public string StorageUntil { get => storageUntil; set => Set(ref storageUntil, value); }
    public LocationInfo? Location { get => location; set => Set(ref location, value); }
    public ItemRow? SelectedItem { get => selectedItem; set => Set(ref selectedItem, value); }
    public ObservableCollection<ItemRow> Items { get; } = [];
    public IReadOnlyList<LocationInfo> Locations { get; }
    public RelayCommand AddItemCommand { get; }
    public RelayCommand RemoveItemCommand { get; }
    public AsyncCommand SaveCommand { get; }

    private static DateTime ParseDate(string text)
    {
        if (!DateTime.TryParseExact(text, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new BusinessException("Дата и время: используйте формат дд.мм.гггг чч:мм.");
        if (TimeZoneInfo.Local.IsInvalidTime(value)) throw new BusinessException("Указанное местное время не существует из-за перевода часов.");
        if (TimeZoneInfo.Local.IsAmbiguousTime(value)) throw new BusinessException("Укажите однозначное местное время вне интервала перевода часов.");
        return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
    }
}
