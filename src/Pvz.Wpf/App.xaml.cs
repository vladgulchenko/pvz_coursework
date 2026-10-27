using System.Globalization;
using System.IO;
using System.Windows;
using Pvz.Infrastructure;
using Pvz.Infrastructure.Data;
using Pvz.Infrastructure.Services;
using Pvz.Wpf.Presentation;
using Pvz.Wpf.ViewModels;
using Pvz.Wpf.Views;

namespace Pvz.Wpf;

public partial class App : Application
{
    private SingleInstanceLock? instanceLock;
    private readonly ShellViewModel shell = new();
    private Session session = null!;
    private AccessService access = null!;
    private AdministrationService administration = null!;
    private OrderService orders = null!;
    private DialogService dialogs = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            var configuredPath = Environment.GetEnvironmentVariable("PVZ_DATABASE_PATH");
            var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PvzCourse", "pvz.db")
                : configuredPath);
            instanceLock = new SingleInstanceLock(path);
            if (!instanceLock.Acquired)
            {
                MessageBox.Show("Программа уже запущена для этой базы данных. Перейдите в открытое окно.", "ПВЗ", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            var factory = PvzDbContextFactory.ForFile(path);
            await new DatabaseInitializer(factory, path).InitializeAsync();
            session = new Session();
            access = new(factory, session);
            administration = new(factory, session);
            orders = new(factory, session, TimeProvider.System);
            dialogs = new(orders, administration, session);
            MainWindow = new ShellWindow { DataContext = shell };
            session.Changed += SessionChanged;
            MainWindow.Show();
            await ShowAuthenticationAsync();
        }
        catch (Exception ex)
        {
            var message = ex is Pvz.Core.BusinessException ? ex.Message
                : "Не удалось открыть базу данных. Проверьте доступ к папке данных и свободное место. Рабочая база не удалялась.";
            MessageBox.Show(message, "Ошибка запуска ПВЗ", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async void SessionChanged(object? sender, EventArgs e)
    {
        if (shell.CurrentPage is MainViewModel previous) previous.Dispose();
        foreach (var window in Windows.OfType<Window>().Where(x => x != MainWindow).ToArray()) window.Close();
        if (session.CurrentUser is null) await ShowAuthenticationAsync();
        else
        {
            var main = new MainViewModel(orders, administration, access, session, dialogs);
            shell.CurrentPage = main;
            await main.InitializeAsync();
        }
    }

    private async Task ShowAuthenticationAsync()
    {
        var auth = new AuthViewModel(access);
        shell.CurrentPage = auth;
        await auth.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (shell.CurrentPage is MainViewModel main) main.Dispose();
        instanceLock?.Dispose();
        base.OnExit(e);
    }
}
