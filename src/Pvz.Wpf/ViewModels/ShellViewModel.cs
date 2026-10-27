using Pvz.Wpf.Presentation;

namespace Pvz.Wpf.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private object? currentPage;
    public object? CurrentPage { get => currentPage; set => Set(ref currentPage, value); }
}
