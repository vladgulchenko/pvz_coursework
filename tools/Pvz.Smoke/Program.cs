using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Pvz.Core;
using Pvz.Infrastructure.Data;
using Pvz.Infrastructure.Services;
using Pvz.Wpf.Presentation;
using Pvz.Wpf.ViewModels;
using Pvz.Wpf.Views;

namespace Pvz.Smoke;

// Separate executable for UI verification. Production startup has no test login or bypass.
internal static class Program
{
    private static readonly string Password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    private static readonly string WorkFolder = Path.Combine(Path.GetTempPath(), "Pvz.Smoke", Guid.NewGuid().ToString("N"));
    private static readonly string ArtifactFolder = Path.GetFullPath(Path.Combine("docs", "report", "screenshots"));
    private static readonly BindingErrorListener BindingErrors = new();
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--startup")) return ProbeStartup();
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        PresentationTraceSources.DataBindingSource.Listeners.Add(BindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var app = new Application
        {
            Resources = new ResourceDictionary { Source = new Uri("pack://application:,,,/Pvz;component/Resources/Theme.xaml") }
        };
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Reuse production views and resources, with separate temporary startup.
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await RunAsync(app);
                Console.WriteLine($"WPF smoke: PASS, {checks} checks. Screenshots: {ArtifactFolder}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Environment.ExitCode = 1;
            }
            finally
            {
                foreach (var window in app.Windows.OfType<Window>().ToArray()) window.Close();
                DeleteRunDirectory(WorkFolder, Path.Combine(Path.GetTempPath(), "Pvz.Smoke"));
                app.Shutdown(Environment.ExitCode);
            }
        }));
        return app.Run();
    }

    private static int ProbeStartup()
    {
        var probeFolder = Path.GetFullPath(Path.Combine(".local", "startup-probe", Guid.NewGuid().ToString("N")));
        var path = Path.Combine(probeFolder, "pvz.db");
        Environment.SetEnvironmentVariable("PVZ_DATABASE_PATH", path);
        var app = new Pvz.Wpf.App();
        app.InitializeComponent();
        var timeout = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += async (_, _) =>
        {
            if (app.MainWindow?.DataContext is ShellViewModel { CurrentPage: AuthViewModel { Ready: true } auth })
            {
                timer.Stop();
                try
                {
                    Check(File.Exists(path), "Production startup creates persistent SQLite database");
                    Check(auth.NeedsSetup, "Production startup displays initial setup without default credentials");
                    Directory.CreateDirectory(ArtifactFolder);
                    await RenderAsync(app.MainWindow, "production-startup.png");
                    auth.Name = "Проверочный администратор";
                    auth.Login = "startup-probe";
                    await auth.SubmitCommand.ExecuteAsync(Password);
                    Check(!auth.NeedsSetup, "Production setup creates administrator through Auth VM");
                    await auth.SubmitCommand.ExecuteAsync(Password);
                    var shell = (ShellViewModel)app.MainWindow.DataContext;
                    await WaitAsync(() => shell.CurrentPage is MainViewModel { IsBusy: false });
                    var main = (MainViewModel)shell.CurrentPage!;
                    Check(main.IsAdministrator, "Production session event opens main screen");
                    var protectedWindow = new LocationsWindow { Owner = app.MainWindow };
                    protectedWindow.Show();
                    main.LogoutCommand.Execute(null);
                    await WaitAsync(() => shell.CurrentPage is AuthViewModel { Ready: true });
                    Check(!protectedWindow.IsVisible && main.Orders.Count == 0 && main.Details is null,
                        "Production logout closes protected windows and clears cards");
                    Check(shell.CurrentPage is AuthViewModel { NeedsSetup: false }, "Existing administrator cannot be configured again");
                    Console.WriteLine($"Production startup/session: PASS, {checks} checks; closing main window normally.");
                    app.MainWindow.Close();
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
            }
            else if (DateTime.UtcNow > timeout)
            {
                timer.Stop();
                Console.Error.WriteLine("Production startup timed out.");
                app.Shutdown(1);
            }
        };
        timer.Start();
        var result = app.Run();
        DeleteRunDirectory(probeFolder, Path.GetFullPath(Path.Combine(".local", "startup-probe")));
        return result;
    }

    private static async Task RunAsync(Application app)
    {
        Directory.CreateDirectory(WorkFolder);
        Directory.CreateDirectory(ArtifactFolder);
        var dbPath = Path.Combine(WorkFolder, "smoke.db");
        var factory = PvzDbContextFactory.ForFile(dbPath);
        await new DatabaseInitializer(factory, dbPath).InitializeAsync();
        var session = new Session();
        var access = new AccessService(factory, session);
        var admin = new AdministrationService(factory, session);
        var orders = new OrderService(factory, session, TimeProvider.System);
        var shell = new ShellViewModel();
        var window = new ShellWindow { DataContext = shell, Width = 1420, Height = 1000 };
        app.MainWindow = window;
        var auth = new AuthViewModel(access);
        shell.CurrentPage = auth;
        window.Show();
        await auth.InitializeAsync();
        Check(auth.NeedsSetup && auth.Ready, "Empty database offers setup");
        await RenderAsync(window, "setup.png");
        auth.Name = "Демонстрационный администратор";
        auth.Login = "demo-admin";
        await auth.SubmitCommand.ExecuteAsync(Password);
        Check(!auth.NeedsSetup && session.CurrentUser is null, "Initial setup requires subsequent sign-in");
        await auth.SubmitCommand.ExecuteAsync(Password);
        Check(session.CurrentUser?.Role == EmployeeRole.Administrator, "Auth VM signs in");

        var locations = new LocationsViewModel(admin, new SmokeDialogs());
        var locationsWindow = new LocationsWindow { Owner = window, DataContext = locations };
        locationsWindow.Show();
        await locations.InitializeAsync();
        locations.Rack = "A"; locations.Cell = "01";
        await locations.SaveCommand.ExecuteAsync();
        Check(locations.Locations.Count == 1, "Location VM creates cell");
        await RenderAsync(locationsWindow, "locations.png");
        locationsWindow.Close();
        var location = (await admin.GetLocationsAsync()).Single();

        var employees = new EmployeesViewModel(admin, session);
        var employeesWindow = new EmployeesWindow { Owner = window, DataContext = employees };
        employeesWindow.Show();
        await employees.InitializeAsync();
        employees.Name = "Демонстрационный оператор"; employees.Login = "demo-operator";
        await employees.SaveCommand.ExecuteAsync(Password);
        Check(employees.Employees.Count == 2, "Employee VM creates operator");
        await RenderAsync(employeesWindow, "employees.png");
        employeesWindow.Close();

        var editor = new OrderEditorViewModel(orders, [location], null)
        {
            Number = "DEMO-001", RecipientName = "Пример Получателя", Phone = "+375000000000", Location = location
        };
        editor.Items[0].Name = "Учебный набор";
        editor.Items[0].Quantity = "2";
        var saved = false;
        editor.Saved += (_, _) => saved = true;
        var editorWindow = new OrderEditorWindow { Owner = window, DataContext = editor };
        editorWindow.Show();
        await RenderAsync(editorWindow, "order-editor.png");
        await editor.SaveCommand.ExecuteAsync();
        Check(saved, "Editor VM persists an order");
        editorWindow.Close();
        var id = (await orders.SearchAsync()).Single().Id;

        var now = DateTime.UtcNow;
        await orders.RegisterAsync(new("DEMO-EXPIRED", "Вымышленный Получатель", "+375000000001",
            now.AddDays(-4), now.AddDays(-1), location.Id, [new("Тестовый товар", 1)]));
        var dialogs = new SmokeDialogs();
        using var main = new MainViewModel(orders, admin, access, session, dialogs);
        shell.CurrentPage = main;
        await main.InitializeAsync();
        Check(main.TotalCount == 2 && main.OverdueCount == 1, "Main VM computes counters and overdue marker");
        main.SelectedOrder = main.Orders.Single(x => x.Id == id);
        await WaitAsync(() => !main.IsBusy && main.HasDetails);
        Check(main.CanIssue && main.CanEdit, "Selected card enables valid operations");
        await RenderAsync(window, "main-window.png");
        Check(FindByAutomationId(window, "OrdersGrid") is DataGrid, "Orders grid is rendered");
        Check(FindByAutomationId(window, "IssueOrder") is Button { IsEnabled: true }, "Issue button binding is enabled");

        dialogs.Confirmed = false;
        await main.IssueCommand.ExecuteAsync();
        Check((await orders.GetAsync(id)).History.Count == 1, "Cancelled confirmation writes nothing");
        dialogs.Confirmed = true;
        await main.IssueCommand.ExecuteAsync();
        Check(main.Details?.Summary.Status == OrderStatus.Issued && main.ReturnLocation is null, "Issue refreshes card and clears placement");
        Check(!main.CanIssue && main.CanStartReturn, "Issued card disables repeated issue");
        main.ReturnReason = "Возврат всего заказа";
        main.ReturnLocation = main.Locations.Single();
        await main.StartReturnCommand.ExecuteAsync();
        Check(main.CanCompleteReturn && main.Details?.StorageLocationId == location.Id, "Return VM requires new placement");
        await main.CompleteReturnCommand.ExecuteAsync();
        Check(main.Details?.Summary.Status == OrderStatus.Returned && main.Details.History.Count == 4, "Full lifecycle has four history records");
        await RenderAsync(window, "order-history.png");

        var failingId = (await orders.SearchAsync("EXPIRED")).Single().Id;
        main.SelectedOrder = main.Orders.Single(x => x.Id == failingId);
        await WaitAsync(() => !main.IsBusy && main.Details?.Summary.Id == failingId);
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailHistory BEFORE INSERT ON OrderStatusHistory BEGIN SELECT RAISE(ABORT, 'smoke failure'); END;");
        main.ReturnReason = "Истек срок хранения";
        main.ReturnLocation = main.Locations.Single();
        await main.StartReturnCommand.ExecuteAsync();
        Check(main.Message.Length > 0 && main.Details?.Summary.Status == OrderStatus.AwaitingPickup && main.Details.History.Count == 1,
            "Failed write reloads actual card from SQLite");

        var gate = new TaskCompletionSource();
        var executions = 0;
        var command = new AsyncCommand(async _ => { executions++; await gate.Task; });
        var first = command.ExecuteAsync();
        await command.ExecuteAsync();
        Check(!command.CanExecute(null) && executions == 1, "AsyncCommand blocks duplicate execution");
        gate.SetResult(); await first;
        Check(command.CanExecute(null), "AsyncCommand unlocks after completion");

        access.Logout();
        main.Dispose();
        Check(session.CurrentUser is null && main.Orders.Count == 0 && main.Details is null, "Logout clears session and cards");
        await access.LoginAsync("demo-operator", Password);
        using var operatorMain = new MainViewModel(orders, admin, access, session, dialogs);
        shell.CurrentPage = operatorMain;
        await operatorMain.InitializeAsync();
        await LayoutAsync(window);
        Check(!operatorMain.IsAdministrator && !operatorMain.EmployeesCommand.CanExecute(null), "Operator cannot invoke administrative commands");
        Check(FindByAutomationId(window, "Employees") is Button { Visibility: Visibility.Collapsed }, "Operator has no admin buttons");
        await new DatabaseInitializer(factory, dbPath).InitializeAsync();
        Check((await orders.GetAsync(id)).Summary.Status == OrderStatus.Returned, "Reinitialization preserves saved lifecycle");
        Check(BindingErrors.Errors.Count == 0, "No WPF binding errors: " + string.Join("; ", BindingErrors.Errors));
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        checks++;
        Console.WriteLine("PASS: " + description);
    }

    private static void DeleteRunDirectory(string folder, string parent)
    {
        var target = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Cleanup path is outside its isolated run directory.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!ready()) throw new TimeoutException("WPF did not reach expected state.");
    }

    private static async Task LayoutAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }

    private static async Task RenderAsync(Window window, string fileName)
    {
        await LayoutAsync(window);
        var surface = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(ArtifactFolder, fileName));
        encoder.Save(output);
    }

    private static DependencyObject? FindByAutomationId(DependencyObject parent, string id)
    {
        if (System.Windows.Automation.AutomationProperties.GetAutomationId(parent) == id) return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindByAutomationId(VisualTreeHelper.GetChild(parent, i), id);
            if (found is not null) return found;
        }
        return null;
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }

    private sealed class SmokeDialogs : IDialogService
    {
        public bool Confirmed { get; set; } = true;
        public bool Confirm(string title, string message) => Confirmed;
        public bool EditOrder(IReadOnlyList<LocationInfo> locations, OrderDetails? order = null) => false;
        public void ShowEmployees() { }
        public void ShowLocations() { }
    }
}
