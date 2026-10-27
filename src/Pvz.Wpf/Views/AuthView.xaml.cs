using System.Windows;
using System.Windows.Controls;
using Pvz.Wpf.ViewModels;

namespace Pvz.Wpf.Views;

public partial class AuthView : UserControl
{
    public AuthView() => InitializeComponent();

    private void Submit(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuthViewModel vm) vm.SubmitCommand.Execute(PasswordInput.Password);
        PasswordInput.Clear();
    }
}
