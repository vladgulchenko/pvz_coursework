using System.Windows;
using Pvz.Wpf.ViewModels;

namespace Pvz.Wpf.Views;

public partial class EmployeesWindow : Window
{
    public EmployeesWindow() => InitializeComponent();

    private void Save(object sender, RoutedEventArgs e)
    {
        if (DataContext is EmployeesViewModel vm) vm.SaveCommand.Execute(PasswordInput.Password);
        PasswordInput.Clear();
    }

    private void ResetPassword(object sender, RoutedEventArgs e)
    {
        if (DataContext is EmployeesViewModel vm) vm.ResetPasswordCommand.Execute(PasswordInput.Password);
        PasswordInput.Clear();
    }
}
