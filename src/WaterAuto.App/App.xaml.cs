using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WaterAuto.App;

/// <summary>
/// Точка входа приложения: применяет сохранённую тему до создания главного окна
/// и не даёт программе молча упасть на необработанном исключении UI-потока.
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        ThemeManager.Initialize();
        WindowChrome.Attach();
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "WaterAuto-crash.log");
        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{e.Exception}\n\n");
        }
#pragma warning disable CA1031 // не суметь записать журнал — не повод не показать сообщение пользователю
        catch (Exception)
        {
            // журнал недоступен — продолжаем, путь всё равно покажем
        }
#pragma warning restore CA1031

        var message =
            "Произошла непредвиденная ошибка программы.\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"Подробности записаны в файл:\n{logPath}\n\n" +
            "Приложение попробует продолжить работу. Сохраните проект и перезапустите программу.";
        MessageBox.Show(message, "Water_Auto — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
