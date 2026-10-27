using Microsoft.Data.Sqlite;
using Pvz.Core;

namespace Pvz.Wpf.Presentation;

public abstract class ScreenViewModel : ObservableObject
{
    private string message = "";
    private bool isBusy;
    public string Message { get => message; protected set => Set(ref message, value); }
    public bool IsBusy { get => isBusy; private set { Set(ref isBusy, value); OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;

    protected async Task RunAsync(Func<Task> operation, Func<Task>? recover = null)
    {
        if (IsBusy) return;
        IsBusy = true;
        Message = "";
        try { await operation(); }
        catch (Exception ex)
        {
            Message = FriendlyError(ex);
            if (recover is not null)
            {
                try { await recover(); }
                catch (Exception refreshError) { Message += " " + FriendlyError(refreshError); }
            }
        }
        finally { IsBusy = false; }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        BusinessException => ex.Message,
        SqliteException { SqliteErrorCode: 5 or 6 } => "База данных занята. Подождите и повторите действие.",
        _ => "Не удалось выполнить операцию. Проверьте доступ к файлу базы данных и повторите действие."
    };
}
