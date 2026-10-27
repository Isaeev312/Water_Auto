using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaterAuto.App;

/// <summary>Режим темы приложения.</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>Акцентный цвет интерфейса.</summary>
public enum AccentColor
{
    Blue,
    Teal,
    Indigo,
}

/// <summary>
/// Настройки приложения: тема, акцент, последние проекты.
/// Хранятся в <c>%APPDATA%\WaterAuto\settings.json</c>.
/// Ошибки ввода-вывода и разбора файла наружу не выбрасываются: при любой проблеме берутся умолчания.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Выбранный режим темы; <see cref="ThemeMode.System"/> — как в Windows.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Акцентный цвет.</summary>
    public AccentColor Accent { get; set; } = AccentColor.Blue;

    /// <summary>
    /// Считать проект сразу после сохранения исходных данных в мастере: пользователь заполнил стандартные данные —
    /// программа сама выполняет расчёт и показывает сводку. Отключается в настройках оформления.
    /// </summary>
    public bool AutoCalculateAfterSave { get; set; } = true;

    /// <summary>Последние открытые проекты (последний — первым).</summary>
#pragma warning disable CA1002, CA2227 // контракт внешнего API: обычный изменяемый List<string> для JSON
    public List<string> RecentProjects { get; set; } = [];
#pragma warning restore CA1002, CA2227

    /// <summary>Путь к файлу настроек: <c>%APPDATA%\WaterAuto\settings.json</c>.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WaterAuto",
        "settings.json");

    /// <summary>
    /// Загружает настройки. Отсутствующий, битый или недоступный файл — это не ошибка:
    /// возвращаются значения по умолчанию.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            if (!Enum.IsDefined(settings.Theme))
            {
                settings.Theme = ThemeMode.System;
            }

            if (!Enum.IsDefined(settings.Accent))
            {
                settings.Accent = AccentColor.Blue;
            }

            settings.RecentProjects ??= [];
            return settings;
        }
#pragma warning disable CA1031 // настройки не должны ронять приложение из-за нечитаемого файла
        catch (Exception)
        {
            return new AppSettings();
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Сохраняет настройки. Ошибки записи (нет прав, диск) подавляются: это не критично для работы программы.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
        }
#pragma warning disable CA1031 // сохранение настроек — фоновая операция, приложение не должно падать
        catch (Exception)
        {
            // тихо игнорируем: настройки не критичны
        }
#pragma warning restore CA1031
    }
}
