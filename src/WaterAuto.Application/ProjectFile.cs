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

    /// <summary>K оросителя, л/(с·МПа^0,5) — по технической документации изделия; 0 — автоподбор из каталога data/catalog.</summary>
    public double SprinklerK { get; init; }

    /// <summary>Положение розетки для автоподбора: up (вверх), down (вниз), horizontal, concealed.</summary>
    public string SprinklerDeflector { get; init; } = "up";

    public string PipeStandard { get; init; } = "ГОСТ 3262-75";

    /// <summary>Стандарт труб, начиная с <see cref="PipeStandardLargeFromDn"/>; пусто — все трубы по <see cref="PipeStandard"/>.</summary>
    public string PipeStandardLarge { get; init; } = "ГОСТ 10704-91";

    public int PipeStandardLargeFromDn { get; init; } = 50;

    /// <summary>Стандарт приварных деталей трубопроводов (отводы, тройники, переходы).</summary>
    public string FittingStandard { get; init; } = "ГОСТ 17375-2001";

    /// <summary>Сортамент для расчёта и спецификации.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PipeSeriesStandard => PipeStandardLarge.Length > 0
        ? Calc.Routing.PipeSeries.Compose(PipeStandard, PipeStandardLarge, PipeStandardLargeFromDn)
        : PipeStandard;

    /// <summary>Высота помещения, м — для табл. 6.3 и примечания 3 к табл. 6.2; null — до 10 м.</summary>
    public double? RoomHeight { get; init; }

    /// <summary>Высота складирования, м — для складов (группы 5–7, табл. 6.2).</summary>
    public double? StorageHeight { get; init; }

    /// <summary>Отметка пола этажа, м — для условия п. 6.2.14 (короба на высоте от пола); null — условие считается выполненным.</summary>
    public double? FloorElevation { get; init; }

    /// <summary>Отметка трубопроводов для аксонометрии, м.</summary>
    public double PipeElevation { get; init; }

    /// <summary>Высота диктующего оросителя над осью насоса H, м.</summary>
    public double DictatingAboveAxis { get; init; }

    /// <summary>Подводящий трубопровод от насоса до точки ПТ_ВВОД: длина, м, и DN.</summary>
    public double SupplyLength { get; init; }

    public int SupplyDn { get; init; } = 100;

    /// <summary>Давление на входе насоса (гарантированный напор по ТУ), МПа; по умолчанию 0,10 МПа (10 м вод. ст.) — уточнить по ТУ.</summary>
    public double InletPressure { get; init; } = 0.10;

    /// <summary>Скорость для подбора диаметров по Б.5, м/с; null — диаметры по табл. Б.4.</summary>
    public double? DesignVelocity { get; init; }

    /// <summary>Предел оросителей на ветви, выбранный проектом (В-09; СП 485 предела не задаёт); null — без проверки.</summary>
    public int? MaxSprinklersPerBranch { get; init; }

    /// <summary>Внутренний противопожарный водопровод, совмещённый с АУП (СН КР 40-04:2025, ADR 0010); null — без ВПВ.</summary>
    public ProjectHydrants? Hydrants { get; init; }
}

/// <summary>Исходные данные ВПВ: признаки здания для табл. Ж.2, ПК для табл. Ж.3, трубопровод до диктующего ПК.</summary>
public sealed class ProjectHydrants
{
    /// <summary>Степень огнестойкости: I, II, III, IV, V.</summary>
    public string FireResistance { get; init; } = "";

    /// <summary>Категория по пожарной опасности: А, Б, В, Г, Д.</summary>
    public string Category { get; init; } = "";

    /// <summary>Класс конструктивной пожарной опасности здания: С0, С1, С2, С3.</summary>
    public string HazardClass { get; init; } = "";

    /// <summary>Объём здания (пожарного отсека), тыс. м³.</summary>
    public double Volume { get; init; }

    /// <summary>Высота здания, м.</summary>
    public double BuildingHeight { get; init; }

    /// <summary>Высота помещения у диктующего ПК, м; null — высота помещения из design.</summary>
    public double? RoomHeight { get; init; }

    public int ValveDn { get; init; } = 50;

    /// <summary>Диаметр спрыска ствола, мм: 13, 16, 19.</summary>
    public int Nozzle { get; init; } = 16;

    /// <summary>Длина рукава, м: 10, 15, 20.</summary>
    public int HoseLength { get; init; } = 20;

    /// <summary>Отметка клапана диктующего ПК над осью насоса, м.</summary>
    public double AboveAxis { get; init; }

    /// <summary>Трубопровод от точки ПТ_ВВОД до диктующего ПК: длина, м, и DN.</summary>
    public double PipeLength { get; init; }

    public int PipeDn { get; init; } = 65;

    /// <summary>Отапливаемая автостоянка закрытого типа: струи по п. 6.4 СНиП 21-02-99* (ADR 0010).</summary>
    public bool IsParking { get; init; }
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
