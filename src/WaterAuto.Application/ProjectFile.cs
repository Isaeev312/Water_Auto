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

    /// <summary>Единый шрифт выпуска: записка, спецификация, «Общие данные», основные надписи и надписи на чертежах.</summary>
    public string Font { get; init; } = Core.DocumentStyle.DefaultFont;

    /// <summary>Ведомость рабочих чертежей основного комплекта (лист «Общие данные»).</summary>
    public IReadOnlyList<ProjectSheet> Sheets { get; init; } =
    [
        new("1", "Общие данные"),
        new("2", "Принципиальная схема насосной станции пожаротушения"),
        new("3", "План насосной станции"),
        new("4", "План этажа. Расстановка оросителей, трубопроводы, пожарные краны"),
        new("5", "Аксонометрическая схема трубопроводов"),
        new("6", "Узлы"),
    ];

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

    /// <summary>Максимальная температура среды в зоне оросителей, °С (п. 6.2.17) — для номинальной температуры срабатывания (табл. 6.4).</summary>
    public double? MaxAmbientTemperature { get; init; }

    /// <summary>Положение розетки для автоподбора: up (вверх), down (вниз), horizontal, concealed.</summary>
    public string SprinklerDeflector { get; init; } = "up";

    public string PipeStandard { get; init; } = "ГОСТ 3262-75";

    /// <summary>Стандарт труб, начиная с <see cref="PipeStandardLargeFromDn"/>; пусто — все трубы по <see cref="PipeStandard"/>.</summary>
    public string PipeStandardLarge { get; init; } = "ГОСТ 10704-91";

    public int PipeStandardLargeFromDn { get; init; } = 50;

    /// <summary>Стандарты приварных деталей трубопроводов по типу: начало наименования → обозначение.</summary>
    public Dictionary<string, string> FittingStandards { get; init; } = new()
    {
        ["Отвод"] = "ГОСТ 17375-2001",
        ["Тройник"] = "ГОСТ 17376-2001",
        ["Переход"] = "ГОСТ 17378-2001",
    };

    /// <summary>Длина резьбовой шпильки подвеса (от перекрытия до хомута), мм.</summary>
    public double HangerRodLength { get; init; } = 300;

    /// <summary>Стандарт сварных соединений трубопроводов.</summary>
    public string WeldStandard { get; init; } = "ГОСТ 16037-80";

    /// <summary>Ссылка в выноске у точки ввода на лист насосной станции.</summary>
    public string InletReference { get; init; } = "см. раздел НС";

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

    /// <summary>Кольцевой питающий трубопровод (рис. Б.2, секции В, Г; ADR 0011).</summary>
    public bool Ring { get; init; }

    /// <summary>На кольце есть нормально закрытые разделительные запорные устройства — полуветвь на 100 % расхода (Б.1.2.16.1).</summary>
    public bool RingValvesClosed { get; init; }

    /// <summary>Предел оросителей на ветви, выбранный проектом (В-09; СП 485 предела не задаёт); null — без проверки.</summary>
    public int? MaxSprinklersPerBranch { get; init; }

    /// <summary>Внутренний противопожарный водопровод, совмещённый с АУП (СН КР 40-04:2025, ADR 0010); null — без ВПВ.</summary>
    public ProjectHydrants? Hydrants { get; init; }

    /// <summary>Помещение насосной станции: отметки и конструкция (слои ПТ_НС*).</summary>
    public ProjectPumpRoom PumpRoom { get; init; } = new();
}

/// <summary>
/// Отметки и конструкция помещения насосной, м (относительно пола насосной). Значения по умолчанию — условные,
/// для пробного проекта; задать по архитектурному разделу.
/// </summary>
public sealed class ProjectPumpRoom
{
    /// <summary>Отметка пола насосной.</summary>
    public double FloorElevation { get; init; }

    /// <summary>Отметка уровня земли (планировочная) — для выводов на фасад (патрубки ГМ, п. 6.10.18).</summary>
    public double GroundElevation { get; init; } = 3.0;

    /// <summary>Заглубление ввода от уровня земли.</summary>
    public double InletDepth { get; init; } = 1.6;

    /// <summary>Высота помещения (до низа перекрытия).</summary>
    public double Height { get; init; } = 3.6;

    /// <summary>Толщина стен.</summary>
    public double WallThickness { get; init; } = 0.3;

    /// <summary>Помещение подземное (п. 6.10.23: не менее двух дренажных насосов).</summary>
    public bool Underground { get; init; } = true;

    /// <summary>Приямок в плане и глубина.</summary>
    public double PitSize { get; init; } = 0.8;

    public double PitDepth { get; init; } = 0.6;

    /// <summary>
    /// Отметка прокладки трассы от ввода в здание до насосной, м; null — под перекрытием на отметке трубопроводов АУП
    /// (pipeElevation), без неё — на 0,5 м ниже высоты помещения насосной.
    /// </summary>
    public double? InletRouteElevation { get; init; }

    /// <summary>Отступ трассы ввода от колонн, м.</summary>
    public double InletRouteClearance { get; init; } = 0.3;
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

    /// <summary>Жилое или общественное здание: строка табл. Ж.1 СН КР 40-04:2025, напр. «g4.seats_over300» (список — data/norms/snkr40_04/table_Zh_1.json).</summary>
    public string? Zh1Row { get; init; }

    /// <summary>Зал с массовым пребыванием людей при сгораемой отделке — на одну струю больше (п. 7.7).</summary>
    public bool ExtraJet { get; init; }

    /// <summary>Радиус действия ПК для расстановки, м; null — длина рукава + радиус компактной струи (В-22).</summary>
    public double? Reach { get; init; }

    /// <summary>Расставлять краны на плане (п. 7.14); false — комплект в спецификации на один кран.</summary>
    public bool Place { get; init; } = true;
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

    /// <summary>Базовое обозначение (шифр) проекта для основной надписи; к нему добавляются марки «ПТ», «ПТ.СО».</summary>
    public string Code { get; init; } = "[Шифр]";

    /// <summary>Стадия (графа 6 основной надписи): «П» — проектная документация, «Р» — рабочая.</summary>
    public string Stage { get; init; } = "Р";

    /// <summary>Наименование здания (сооружения) — графа 3 основной надписи.</summary>
    public string Building { get; init; } = "[Наименование здания]";

    /// <summary>Подписи основной надписи (должность, фамилия); пусто — разработчик и руководитель с титульного листа, «Н. контр.».</summary>
    public IReadOnlyList<ProjectSignature> Signatures { get; init; } = [];
}

/// <summary>Подпись в основной надписи: должность и фамилия.</summary>
public sealed record ProjectSignature(string Role, string Name);

/// <summary>Лист основного комплекта: номер, наименование, примечание.</summary>
public sealed record ProjectSheet(string Sheet, string Name, string Note = "");
