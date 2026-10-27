using System.Windows;
using Pvz.Core;
using Pvz.Wpf.ViewModels;
using Pvz.Wpf.Views;

namespace Pvz.Wpf.Presentation;

public sealed class DialogService(IOrderService orders, IAdministrationService administration, ISession session) : IDialogService
{
    public bool Confirm(string title, string message) => MessageBox.Show(
        Application.Current.Windows.OfType<Window>().LastOrDefault(x => x.IsActive) ?? Application.Current.MainWindow,
        message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool EditOrder(IReadOnlyList<LocationInfo> locations, OrderDetails? order = null)
    {
        var vm = new OrderEditorViewModel(orders, locations, order);
        var window = new OrderEditorWindow { Owner = Application.Current.MainWindow, DataContext = vm };
        vm.Saved += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true;
    }

    public void ShowEmployees()
    {
        var vm = new EmployeesViewModel(administration, session);
        var window = new EmployeesWindow { Owner = Application.Current.MainWindow, DataContext = vm };
        window.Loaded += async (_, _) => await vm.InitializeAsync();
        window.ShowDialog();
    }

    public void ShowLocations()
    {
        var vm = new LocationsViewModel(administration, this);
        var window = new LocationsWindow { Owner = Application.Current.MainWindow, DataContext = vm };
        window.Loaded += async (_, _) => await vm.InitializeAsync();
        window.ShowDialog();
    }
}
