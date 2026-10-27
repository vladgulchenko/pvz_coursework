using Pvz.Core;
using Pvz.Wpf.Presentation;

namespace Pvz.Wpf.ViewModels;

public sealed class AuthViewModel : ScreenViewModel
{
    private readonly IAccessService access;
    private bool needsSetup;
    private bool ready;
    private string name = "";
    private string login = "";

    public AuthViewModel(IAccessService access)
    {
        this.access = access;
        SubmitCommand = new(parameter => RunAsync(async () =>
        {
            if (!ready) throw new BusinessException("Проверка базы данных ещё не завершена.");
            var password = parameter as string ?? "";
            if (NeedsSetup)
            {
                await access.CreateInitialAdministratorAsync(Name, Login, password);
                NeedsSetup = false;
                Message = "Администратор создан. Введите пароль для входа.";
            }
            else await access.LoginAsync(Login, password);
        }));
        RetryCommand = new(_ => InitializeAsync());
    }

    public bool NeedsSetup
    {
        get => needsSetup;
        private set { Set(ref needsSetup, value); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(SubmitText)); }
    }
    public bool Ready { get => ready; private set => Set(ref ready, value); }
    public string Name { get => name; set => Set(ref name, value); }
    public string Login { get => login; set => Set(ref login, value); }
    public string Title => NeedsSetup ? "Настройка пункта выдачи" : "Вход в систему";
    public string Subtitle => NeedsSetup ? "Создайте первого администратора. Пароль — от 8 символов." : "Войдите с учётной записью сотрудника.";
    public string SubmitText => NeedsSetup ? "Создать администратора" : "Войти";
    public AsyncCommand SubmitCommand { get; }
    public AsyncCommand RetryCommand { get; }

    public Task InitializeAsync() => RunAsync(async () =>
    {
        Ready = false;
        NeedsSetup = await access.NeedsSetupAsync();
        Ready = true;
    });
}
