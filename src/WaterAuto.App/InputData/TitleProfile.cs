using System.Text.Json.Nodes;

namespace WaterAuto.App.InputData;

/// <summary>
/// Профиль титульного листа: организация, подписи и шифр прошлого сохранённого проекта запоминаются
/// в настройках приложения (<see cref="AppSettings"/>, файл %APPDATA%\WaterAuto\settings.json) и подставляются
/// в новый проект, где стоят заглушки модели («[Наименование организации]», «[ФИО]» и т. п.).
/// Профиль — удобство, а не данные норм: значения только переносятся между проектами, ничего не вычисляется.
/// </summary>
internal static class TitleProfile
{
    private const string TitlePath = "title";

    /// <summary>Поле мастера относится к профилю титульного листа.</summary>
    public static bool Covers(string path, string key) =>
        string.Equals(path, TitlePath, StringComparison.Ordinal)
        && key is "organization" or "author" or "supervisor" or "cityYear" or "code" or "building" or "stage";

    /// <summary>Значение профиля для поля; пустая строка — в профиле ничего не запомнено.</summary>
    public static string Value(string path, string key)
    {
        if (!Covers(path, key))
        {
            return "";
        }

        var settings = ThemeManager.Settings;
        return key switch
        {
            "organization" => settings.TitleOrganization,
            "author" => settings.TitleAuthor,
            "supervisor" => settings.TitleSupervisor,
            "cityYear" => settings.TitleCityYear,
            "code" => settings.TitleCode,
            "building" => settings.TitleBuilding,
            _ => settings.TitleStage,
        };
    }

    /// <summary>
    /// Подставить значение профиля при открытии окна: только если в проекте пусто или стоит заглушка модели
    /// и значение профиля отличается от показанного. null — оставить значение проекта (или заглушку) как есть.
    /// В JSON проекта ничего не пишется: значение попадёт в файл только при сохранении.
    /// </summary>
    public static string? Prefill(string projectValue, string path, string key, JsonObject modelDefaults)
    {
        if (!Covers(path, key))
        {
            return null;
        }

        var profile = Value(path, key).Trim();
        if (profile.Length == 0)
        {
            return null;
        }

        var current = projectValue.Trim();
        var placeholder = Placeholder(modelDefaults, key);
        var projectIsBlank = current.Length == 0
            || (placeholder.Length > 0 && string.Equals(current, placeholder, StringComparison.Ordinal));
        if (!projectIsBlank || string.Equals(profile, current, StringComparison.Ordinal))
        {
            return null;
        }

        return profile;
    }

    /// <summary>
    /// Запомнить значения титульного листа из успешно сохранённого проекта. Пустые значения и заглушки модели
    /// прежний профиль не затирают (пользователь мог стереть поле — данные профиля сохраняются).
    /// При изменении профиль сразу сохраняется на диск.
    /// </summary>
    public static void Remember(JsonObject root, JsonObject modelDefaults)
    {
        var settings = ThemeManager.Settings;
        var changed = false;
        changed |= RememberValue(root, modelDefaults, "organization", settings.TitleOrganization, value => settings.TitleOrganization = value);
        changed |= RememberValue(root, modelDefaults, "author", settings.TitleAuthor, value => settings.TitleAuthor = value);
        changed |= RememberValue(root, modelDefaults, "supervisor", settings.TitleSupervisor, value => settings.TitleSupervisor = value);
        changed |= RememberValue(root, modelDefaults, "cityYear", settings.TitleCityYear, value => settings.TitleCityYear = value);
        changed |= RememberValue(root, modelDefaults, "code", settings.TitleCode, value => settings.TitleCode = value);
        changed |= RememberValue(root, modelDefaults, "building", settings.TitleBuilding, value => settings.TitleBuilding = value);
        changed |= RememberValue(root, modelDefaults, "stage", settings.TitleStage, value => settings.TitleStage = value);
        if (changed)
        {
            settings.Save();
        }
    }

    private static bool RememberValue(
        JsonObject root,
        JsonObject modelDefaults,
        string key,
        string current,
        Action<string> assign)
    {
        var value = InputJson.Text(InputJson.Find(root, TitlePath, key)).Trim();
        if (value.Length == 0 || IsPlaceholder(value, modelDefaults, key))
        {
            return false;
        }

        if (string.Equals(value, current, StringComparison.Ordinal))
        {
            return false;
        }

        assign(value);
        return true;
    }

    private static bool IsPlaceholder(string value, JsonObject modelDefaults, string key)
    {
        var placeholder = Placeholder(modelDefaults, key);
        return placeholder.Length > 0 && string.Equals(value, placeholder, StringComparison.Ordinal);
    }

    private static string Placeholder(JsonObject modelDefaults, string key) =>
        InputJson.Text(InputJson.Find(modelDefaults, TitlePath, key)).Trim();
}
