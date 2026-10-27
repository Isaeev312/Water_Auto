using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WaterAuto.App;

/// <summary>
/// Тёмный заголовок окна под тёмную тему Windows 10/11 (DWM immersive dark mode). Оформление окна
/// (рамка, кнопки) остаётся системным; на старых сборках Windows вызов тихо пропускается.
/// </summary>
internal static class WindowChrome
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 — Windows 10 20H1+/11, 19 — Windows 10 1809–1909.
    private const int DarkModeAttribute = 20;
    private const int DarkModeAttributeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Подписка на появление окон и смену темы: применяет цвет заголовка ко всем окнам приложения.</summary>
    internal static void Attach()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        ThemeManager.Changed += (_, _) => ApplyToOpenWindows();
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            Apply(window);
        }
    }

    /// <summary>Применить текущую тему к заголовку открытых окон (после переключения темы).</summary>
    internal static void ApplyToOpenWindows()
    {
        if (System.Windows.Application.Current is not { } application)
        {
            return;
        }

        foreach (Window window in application.Windows)
        {
            Apply(window);
        }
    }

    private static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var dark = ThemeManager.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DarkModeAttribute, ref dark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, DarkModeAttributeLegacy, ref dark, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // Старые версии Windows без dwmapi — заголовок остаётся светлым, функциональность не страдает.
        }
        catch (EntryPointNotFoundException)
        {
            // Нет нужной точки входа DWM — то же самое.
        }
    }
}
