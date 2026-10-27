using Pvz.Core;

namespace Pvz.Wpf.Presentation;

public interface IDialogService
{
    bool Confirm(string title, string message);
    bool EditOrder(IReadOnlyList<LocationInfo> locations, OrderDetails? order = null);
    void ShowEmployees();
    void ShowLocations();
}
