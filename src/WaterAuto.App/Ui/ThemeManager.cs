using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;

namespace WaterAuto.App;

/// <summary>
/// Управление темой и акцентом: подменяет <see cref="ResourceDictionary"/> темы и акцента
/// в <see cref="Application.Resources"/>, сохраняет выбор в <see cref="AppSettings"/>.
/// Кисти в Controls.xaml подключены через DynamicResource, поэтому смена темы применяется
/// к уже открытым окнам без перезапуска.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightThemeUri = new("pack://application:,,,/WaterAuto.App;component/Themes/Tokens.Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkThemeUri = new("pack://application:,,,/WaterAuto.App;component/Themes/Tokens.Dark.xaml", UriKind.Absolute);

    private static readonly Dictionary<AccentColor, Uri> AccentUris = new()
    {
        [AccentColor.Blue] = new Uri("pack://application:,,,/WaterAuto.App;component/Themes/Accent.Blue.xaml", UriKind.Absolute),
        [AccentColor.Teal] = new Uri("pack://application:,,,/WaterAuto.App;component/Themes/Accent.Teal.xaml", UriKind.Absolute),
        [AccentColor.Indigo] = new Uri("pack://application:,,,/WaterAuto.App;component/Themes/Accent.Indigo.xaml", UriKind.Absolute),
    };

    private static AppSettings _settings = AppSettings.Load();
    private static bool _isDark;
    private static bool _systemHook;

    /// <summary>Текущие настройки приложения (тема, акцент, последние проекты).</summary>
    public static AppSettings Settings => _settings;

    /// <summary>Фактическая тема после разрешения <see cref="ThemeMode.System"/>.</summary>
    public static bool IsDark => _isDark;

    /// <summary>Событие изменения темы или акцента.</summary>
    public static event EventHandler? Changed;

    /// <summary>Загружает настройки и немедленно применяет их. Вызывается из <c>App.OnStartup</c>.</summary>
    public static void Initialize()
    {
        _settings = AppSettings.Load();
        Apply();
        HookSystemTheme();
    }

    // В режиме «как в системе» следим за сменой оформления Windows (светлая/тёмная) и переключаемся на лету.
    private static void HookSystemTheme()
    {
        if (_systemHook)
        {
            return;
        }

        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _systemHook = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Нет доступа к системным событиям (служба, урезанная среда) — тема просто не будет меняться на лету.
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme != ThemeMode.System)
        {
            return;
        }

        if (IsSystemDarkTheme() != _isDark)
        {
            Apply();
        }
    }

    /// <summary>
    /// Переключает светлую и тёмную темы. Если сейчас выбран режим «как в системе»,
    /// переключаемся на противоположную фактической теме.
    /// </summary>
    public static void ToggleTheme()
    {
        _settings.Theme = _settings.Theme switch
        {
            ThemeMode.System => _isDark ? ThemeMode.Light : ThemeMode.Dark,
            ThemeMode.Dark => ThemeMode.Light,
            _ => ThemeMode.Dark,
        };

        _settings.Save();
        Apply();
    }

    /// <summary>Сохраняет и применяет выбранный режим темы (светлая, тёмная, как в системе).</summary>
    public static void SetTheme(ThemeMode mode)
    {
        _settings.Theme = mode;
        _settings.Save();
        Apply();
    }

    /// <summary>Сохраняет и применяет акцентный цвет.</summary>
    public static void SetAccent(AccentColor accent)
    {
        _settings.Accent = accent;
        _settings.Save();
        Apply();
    }

    private static void Apply()
    {
        _isDark = _settings.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemDarkTheme(),
        };

        // Полное имя: в пространстве имён WaterAuto.App простой идентификатор Application
        // разрешается в соседнее пространство имён WaterAuto.Application (ссылка на проект).
        if (System.Windows.Application.Current is not { } application)
        {
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        ApplyDictionary(dictionaries, "Tokens.", 0, _isDark ? DarkThemeUri : LightThemeUri);
        ApplyDictionary(dictionaries, "Accent.", 1, AccentUris[_settings.Accent]);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Находит словарь темы/акцента по имени файла (порядок в App.xaml фиксирован, но ищем по Source:
    /// так подмена не зависит от индекса) и заменяет его. Индекс — запасной вариант.
    /// </summary>
    private static void ApplyDictionary(Collection<ResourceDictionary> dictionaries, string fileMarker, int fallbackIndex, Uri target)
    {
        var index = fallbackIndex;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null && source.Contains(fileMarker, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || index >= dictionaries.Count)
        {
            return;
        }

        var fileName = target.OriginalString[(target.OriginalString.LastIndexOf('/') + 1)..];
        var current = dictionaries[index].Source?.OriginalString;
        if (current is not null && current.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        dictionaries[index] = new ResourceDictionary { Source = target };
    }

    /// <summary>Тёмная тема Windows: HKCU\...\Themes\Personalize\AppsUseLightTheme = 0.</summary>
    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalize?.GetValue("AppsUseLightTheme") is int useLightTheme && useLightTheme == 0;
        }
#pragma warning disable CA1031 // определение темы Windows не должно ронять запуск приложения
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }
}
