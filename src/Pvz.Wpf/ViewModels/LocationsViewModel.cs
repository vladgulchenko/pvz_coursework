using System.Collections.ObjectModel;
using Pvz.Core;
using Pvz.Wpf.Presentation;

namespace Pvz.Wpf.ViewModels;

public sealed class LocationsViewModel : ScreenViewModel
{
    private readonly IAdministrationService administration;
    private LocationInfo? selected;
    private string rack = "";
    private string cell = "";

    public LocationsViewModel(IAdministrationService administration, IDialogService dialogs)
    {
        this.administration = administration;
        NewCommand = new(() => { Selected = null; Rack = ""; Cell = ""; });
        SaveCommand = new(_ => RunAsync(async () =>
        {
            await administration.SaveLocationAsync(Selected?.Id, Rack, Cell);
            await ReloadAsync();
            Message = "Место хранения сохранено.";
        }, ReloadAsync));
        DeleteCommand = new(_ => RunAsync(async () =>
        {
            var location = Selected ?? throw new BusinessException("Выберите место хранения.");
            if (!dialogs.Confirm("Удаление места", $"Удалить место хранения {location.Label}?")) return;
            await administration.DeleteLocationAsync(location.Id);
            await ReloadAsync();
        }, ReloadAsync));
    }

    public ObservableCollection<LocationInfo> Locations { get; } = [];
    public LocationInfo? Selected
    {
        get => selected;
        set { if (!Set(ref selected, value)) return; Rack = value?.Rack ?? ""; Cell = value?.Cell ?? ""; }
    }
    public string Rack { get => rack; set => Set(ref rack, value); }
    public string Cell { get => cell; set => Set(ref cell, value); }
    public RelayCommand NewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public Task InitializeAsync() => RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        var id = Selected?.Id;
        var rows = await administration.GetLocationsAsync();
        Locations.Clear();
        foreach (var location in rows) Locations.Add(location);
        Selected = rows.FirstOrDefault(x => x.Id == id);
    }
}
