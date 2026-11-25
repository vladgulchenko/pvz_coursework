using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Pvz.Core;
using Pvz.Infrastructure;
using Pvz.Infrastructure.Data;
using Pvz.Infrastructure.Services;
using Pvz.Wpf.ViewModels;

namespace Pvz.Smoke;

internal static partial class Program
{
    private static int RunOperationalProbe(string[] args) => args[0] switch
    {
        "--instance-lock" => ProbeCrossProcessLock(),
        "--lock-child" when args.Length == 2 => ProbeChildLock(args[1]),
        "--restart" => ProbeProcessRestart(),
        "--restart-write" when args.Length == 2 => ProbePersistentApplication(args[1], write: true),
        "--restart-read" when args.Length == 2 => ProbePersistentApplication(args[1], write: false),
        _ => throw new ArgumentException("Unknown operational probe arguments.")
    };

    private static int ProbeChildLock(string path)
    {
        using var instance = new SingleInstanceLock(path);
        Console.WriteLine(instance.Acquired ? "Lock acquired." : "Lock rejected.");
        return instance.Acquired ? 0 : 2;
    }

    private static int ProbeCrossProcessLock()
    {
        var path = Path.Combine(Path.GetTempPath(), "Pvz.Smoke.Lock", Guid.NewGuid().ToString("N") + ".db");
        using (var instance = new SingleInstanceLock(path))
        {
            if (!instance.Acquired) throw new InvalidOperationException("Could not acquire the isolated test lock.");
            Check(RunChild("--lock-child", path.ToUpperInvariant()).ExitCode == 2,
                "Another process cannot acquire the same database lock");
            Check(RunChild("--lock-child", path + "-other").ExitCode == 0,
                "Another database can be opened by a different process");
        }
        Check(RunChild("--lock-child", path).ExitCode == 0,
            "Database lock becomes available after its owner releases it");
        Console.WriteLine($"Cross-process lock: PASS, {checks} checks.");
        return 0;
    }

    private static int ProbeProcessRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "Pvz.Smoke.Restart");
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var write = RunChild("--restart-write", folder);
            Console.Write(write.Output);
            if (write.ExitCode != 0) throw new InvalidOperationException("The first application process failed.");
            var read = RunChild("--restart-read", folder);
            Console.Write(read.Output);
            if (read.ExitCode != 0) throw new InvalidOperationException("The restarted application process failed.");
            var count = (write.Output + read.Output).Split('\n').Count(line => line.StartsWith("PASS: ", StringComparison.Ordinal));
            Console.WriteLine($"Process restart: PASS, {count} checks across two normal application launches.");
            return 0;
        }
        finally { DeleteRunDirectory(folder, root); }
    }

    private static int ProbePersistentApplication(string folder, bool write)
    {
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Pvz.Smoke.Restart"));
        var full = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(full), expectedRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new ArgumentException("Persistence probes only use their isolated temporary directory.");
        if (Environment.GetEnvironmentVariable("PVZ_SMOKE_PASSWORD") is null)
            throw new InvalidOperationException("Run this probe through --restart.");

        var path = Path.Combine(full, "persistent.db");
        Environment.SetEnvironmentVariable("PVZ_DATABASE_PATH", path);
        var app = new Pvz.Wpf.App();
        app.InitializeComponent();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += async (_, _) =>
        {
            if (app.MainWindow?.DataContext is ShellViewModel { CurrentPage: AuthViewModel { Ready: true } auth } shell)
            {
                timer.Stop();
                try
                {
                    Check(File.Exists(path) && auth.NeedsSetup == write,
                        write ? "First process initializes an empty database" : "Second process recognizes the existing administrator");
                    auth.Login = "restart-admin";
                    if (write)
                    {
                        auth.Name = "Проверочный администратор";
                        await auth.SubmitCommand.ExecuteAsync(Password);
                        if (auth.NeedsSetup) throw new InvalidOperationException("Administrator setup failed.");
                    }
                    await auth.SubmitCommand.ExecuteAsync(Password);
                    await WaitAsync(() => shell.CurrentPage is MainViewModel { IsBusy: false });
                    var main = (MainViewModel)shell.CurrentPage!;
                    Check(main.IsAdministrator, "Application process signs in using persisted credentials");
                    if (write)
                    {
                        var factory = PvzDbContextFactory.ForFile(path);
                        var session = new Session();
                        await new AccessService(factory, session).LoginAsync("restart-admin", Password);
                        var admin = new AdministrationService(factory, session);
                        var orders = new OrderService(factory, session, TimeProvider.System);
                        var location = await admin.SaveLocationAsync(null, "A", "01");
                        var now = DateTime.UtcNow;
                        var id = await orders.RegisterAsync(new("RESTART-001", "Вымышленный получатель", "+375000000000",
                            now.AddMinutes(-1), now.AddDays(2), location, [new("Тестовый набор", 2)]));
                        await orders.IssueAsync(id, true);
                        await orders.StartReturnAsync(id, "Возврат всего заказа", location, true);
                        await orders.CompleteReturnAsync(id, true);
                        await main.RefreshCommand.ExecuteAsync();
                    }
                    var row = main.Orders.Single(x => x.Number == "RESTART-001");
                    main.SelectedOrder = row;
                    await WaitAsync(() => !main.IsBusy && main.Details?.Summary.Id == row.Id);
                    Check(main.Details is { Summary.Status: OrderStatus.Returned, StorageLocationId: null, History.Count: 4 },
                        write ? "First process saves a complete lifecycle before closing" : "Second process restores status, empty placement and four history records");
                    if (!write)
                        Check(main.Details!.Items is [{ Name: "Тестовый набор", Quantity: 2 }],
                            "Second process restores the saved order items");
                    app.MainWindow.Close();
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
            }
            else if (DateTime.UtcNow > deadline)
            {
                timer.Stop();
                Console.Error.WriteLine("Persistence application phase timed out.");
                app.Shutdown(1);
            }
        };
        timer.Start();
        return app.Run();
    }

    private static (int ExitCode, string Output) RunChild(params string[] args)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment["PVZ_SMOKE_PASSWORD"] = Password;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test child process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException("The test child process did not exit within 30 seconds.");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
    }
}
