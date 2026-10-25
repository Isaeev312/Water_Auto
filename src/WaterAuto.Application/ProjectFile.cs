using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaterAuto.Application;

/// <summary>
/// Проект по чертежу (`*.waproj`, README §6.3): путь к DWG/DXF по соглашению о слоях и исходные данные расчёта.
/// Числа норм здесь не задаются — только данные объекта и решения проектировщика.
/// </summary>
public sealed record ProjectFile
{
    public int SchemaVersion { get; init; } = 1;

    public string Name { get; init; } = "";

    /// <summary>Чертёж плана; путь относительно файла проекта.</summary>
    public string Drawing { get; init; } = "";

    public ProjectDesign Design { get; init; } = new();

    public ProjectTitle Title { get; init; } = new();

    /// <summary>ID реестра литературы для списка источников; пусто — действующие нормы реестра.</summary>
    public IReadOnlyList<string> Literature { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица в файле — как есть
    };

    private static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static ProjectFile Load(string path)
    {
        var project = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"Пустой файл проекта: {path}");
        if (project.Drawing.Length == 0)
        {
            throw new InvalidDataException("В проекте не задан чертёж (поле drawing).");
        }

        return project;
    }

    public static string ToJson(ProjectFile project) => JsonSerializer.Serialize(project, Indented);
}

/// <summary>Исходные данные расчёта.</summary>
public sealed record ProjectDesign
{
    /// <summary>Группа помещений по приложению А СП 485 (строка табл. 6.1).</summary>
    public string RoomGroup { get; init; } = "2";

    /// <summary>Класс пожарной опасности конструкций: K0, K1, K2, K3, Unrated.</summary>
    public Core.ConstructionClass ConstructionClass { get; init; } = Core.ConstructionClass.K0;

    /// <summary>Шаг строже табл. 6.1 (профиль заказчика), м; null — по таблице.</summary>
    public double? MaxSpacing { get; init; }

    public string SprinklerName { get; init; } = "Ороситель спринклерный водяной";

    public string SprinklerModel { get; init; } = "";

    /// <summary>K оросителя, л/(с·МПа^0,5) — по технической документации изделия.</summary>
    public double SprinklerK { get; init; }

    public string PipeStandard { get; init; } = "ГОСТ 3262";

    /// <summary>Отметка трубопроводов для аксонометрии, м.</summary>
    public double PipeElevation { get; init; }

    /// <summary>Высота диктующего оросителя над осью насоса H, м.</summary>
    public double DictatingAboveAxis { get; init; }

    /// <summary>Подводящий трубопровод от насоса до точки ПТ_ВВОД: длина, м, и DN.</summary>
    public double SupplyLength { get; init; }

    public int SupplyDn { get; init; } = 100;

    /// <summary>Давление на входе насоса (гарантированный напор по ТУ), МПа.</summary>
    public double InletPressure { get; init; }

    /// <summary>Скорость для подбора диаметров по Б.5, м/с; null — диаметры по табл. Б.4.</summary>
    public double? DesignVelocity { get; init; }
}

/// <summary>Титульный лист записки.</summary>
public sealed record ProjectTitle
{
    public string Organization { get; init; } = "[Наименование организации]";

    public string Subject { get; init; } = "Автоматическая установка водяного пожаротушения";

    public string Document { get; init; } = "Пояснительная записка";

    public string Author { get; init; } = "[ФИО]";

    public string Supervisor { get; init; } = "[ФИО]";

    public string CityYear { get; init; } = "[Город, год]";
}
