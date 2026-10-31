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

    /// <summary>
    /// Водяная АУПТ в проекте (по умолчанию — да, как во всех прежних проектах). false — только другие разделы (АПС, СОУЭ…):
    /// раздел design не используется, ввод ПТ_ВВОД на чертеже не нужен, тома АУПТ нет.
    /// </summary>
    public bool Aupt { get; init; } = true;

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

    /// <summary>Газовое пожаротушение объёмным способом (СП 485, разд. 9); null — не проектируется.</summary>
    public ProjectGas? Gas { get; init; }

    /// <summary>Порошковое пожаротушение модульного типа (СП 485, разд. 10, прил. И); null — не проектируется.</summary>
    public ProjectPowder? Powder { get; init; }

    /// <summary>Пожарная сигнализация (ADR 0012, этап 2); null — раскладка ИП не выполняется.</summary>
    public ProjectAlarm? Alarm { get; init; }

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

    /// <summary>Проект из строки JSON (тем же форматом, что и файл проекта) — для проверок без записи на диск.</summary>
    public static ProjectFile FromJson(string json) =>
        JsonSerializer.Deserialize<ProjectFile>(json, Options) ?? throw new InvalidDataException("Пустой JSON проекта.");
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

    /// <summary>Скорость для подбора диаметров по Б.5, м/с; null — скорость из data/catalog/pipe_sizes.json (defaultDesignVelocity), без неё — табл. Б.4.</summary>
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

    /// <summary>Комплектная насосная установка: ручной выбор из каталога (data/catalog/pump_sets_*.json) или автоподбор по Q и H.</summary>
    public ProjectPumpStation PumpStation { get; init; } = new();

    /// <summary>Жокей-насос: расход и уставки — решение проектировщика (норма их не задаёт, вопрос В-07); null — не заданы.</summary>
    public ProjectJockey? Jockey { get; init; }

    /// <summary>Резервуар пожарного запаса пользователя (габариты и мёртвый слой); null — без сравнения с требуемым объёмом.</summary>
    public ProjectTank? Tank { get; init; }
}

/// <summary>
/// Комплектная насосная установка проекта: конкретная установка каталога вместо автоподбора по требуемым подаче и напору.
/// Насосы, прибор управления, обвязка с арматурой и КИП входят в установку — количество насосов в выпуске берётся по ней.
/// </summary>
public sealed record ProjectPumpStation
{
    /// <summary>
    /// Обозначение установки из каталога (designation, напр. «Hydro MX-A 2/1 CR 45-4»); пусто — установка подбирается
    /// автоматически по расчётным Q и H. Неизвестное обозначение — ошибка проекта (Issue pump.setNotFound).
    /// </summary>
    public string SetDesignation { get; init; } = "";
}

/// <summary>
/// Жокей-насос: расход и уставки — решение проектировщика (норма их не задаёт, вопрос В-07). Пустые поля не заданы;
/// параметры не влияют на расчёт Q/H и спецификацию — только на замечания и пояснительную записку.
/// </summary>
public sealed record ProjectJockey
{
    /// <summary>Расход жокей-насоса, л/с; норма его не задаёт (В-07) — по опросному листу изготовителя.</summary>
    public double? FlowLitersPerSecond { get; init; }

    /// <summary>Давление включения жокей-насоса, МПа; норма уставку не задаёт (В-07).</summary>
    public double? StartPressureMpa { get; init; }

    /// <summary>Давление отключения жокей-насоса, МПа; норма уставку не задаёт (В-07).</summary>
    public double? StopPressureMpa { get; init; }
}

/// <summary>Резервуар пожарного запаса: габариты и мёртвый слой, м (ARCHITECTURE §5).</summary>
public sealed class ProjectTank
{
    /// <summary>Длина в плане, м.</summary>
    public double Length { get; init; }

    /// <summary>Ширина в плане, м.</summary>
    public double Width { get; init; }

    /// <summary>Высота воды, м.</summary>
    public double Height { get; init; }

    /// <summary>Мёртвый слой, м — вода ниже него насосом не используется.</summary>
    public double DeadLayer { get; init; }
}

/// <summary>
/// Отметки и конструкция помещения насосной, м (относительно пола насосной). Значения по умолчанию — условные,
/// для пробного проекта; задать по архитектурному разделу.
/// </summary>
public sealed class ProjectPumpRoom
{
    /// <summary>Отметка пола насосной.</summary>
    public double FloorElevation { get; init; }

    /// <summary>
    /// Отметка уровня земли (планировочная) — для выводов на фасад (патрубки ГМ, п. 6.10.18).
    /// По умолчанию пол насосной и земля на одном уровне (план этажа без разрезов); для заглублённой насосной задаётся в проекте.
    /// </summary>
    public double GroundElevation { get; init; }

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

/// <summary>Исходные данные АПС: где ставить извещатели и какие — решения проектировщика (ADR 0012, п. 6; вопрос А-02 программа не решает).</summary>
public sealed class ProjectAlarm
{
    public IReadOnlyList<ProjectAlarmZone> Zones { get; init; } = [];

    /// <summary>ID модели ручного извещателя из каталога извещателей; null — первая ручная модель (с замечанием).</summary>
    public string? ManualModel { get; init; }

    /// <summary>Точка установки приборов (пожарный пост: контроллеры, РИП) на плане, мм; null — кабельный журнал не выпускается.</summary>
    public ProjectPoint? Panel { get; init; }

    /// <summary>Системы вентиляции от раздела ОВ для команд АППЗ; null — ТЗ на вентиляцию не выпускается.</summary>
    public ProjectHvac? Hvac { get; init; }

    /// <summary>СОУЭ: способ оповещения и модели оповещателей для помещений из zones; null — СОУЭ не проектируется.</summary>
    public ProjectWarning? Warning { get; init; }

    /// <summary>
    /// Уровни (этажи) объекта для АПС: у каждого свой чертёж и отметка пола; пусто — один уровень на основном чертеже проекта.
    /// Раскладка и лист плана — по каждому уровню, питание, адреса, спецификация и кабельный журнал — общие.
    /// </summary>
    public IReadOnlyList<ProjectAlarmLevel> Levels { get; init; } = [];

    /// <summary>Ведомость рабочих чертежей тома АПС (лист «Общие данные»); при нескольких уровнях лист плана повторяется на каждый.</summary>
    public IReadOnlyList<ProjectSheet> Sheets { get; init; } =
    [
        new("1", "Общие данные"),
        new("2", "План расстановки извещателей и ручных извещателей"),
        new("3", "Структурная схема"),
    ];
}

/// <summary>Уровень (этаж) для АПС: имя для листа и ведомости, чертёж (null — основной чертёж проекта), отметка пола, м.</summary>
public sealed class ProjectAlarmLevel
{
    public string Name { get; init; } = "";

    public string? Drawing { get; init; }

    /// <summary>Отметка пола уровня, м — длина стояков ДПЛС между уровнями в кабельном журнале; null — стояк не учтён (замечание).</summary>
    public double? Elevation { get; init; }
}

/// <summary>Исходные данные АППЗ от раздела ОВ: общеобменные системы, дымовые зоны, нормально открытые клапаны.</summary>
public sealed class ProjectHvac
{
    public IReadOnlyList<string> GeneralVentilation { get; init; } = [];

    public IReadOnlyList<ProjectSmokeZone> SmokeZones { get; init; } = [];

    public int NormallyOpenDampers { get; init; }

    /// <summary>ID релейного блока из каталога оповещателей для команд; null — первый в каталоге.</summary>
    public string? Relay { get; init; }
}

/// <summary>СОУЭ (СП 3.13130.2026): способ оповещения — решение проектировщика по табл. Б.1, фон — по табл. В.1, модели — из каталога.</summary>
public sealed class ProjectWarning
{
    public Core.WarningMethod Method { get; init; } = Core.WarningMethod.Sound;

    /// <summary>ID строки табл. Б.1 (data/norms/sp3/table_B_1_method.json), по которой проверяется способ; null — не проверять.</summary>
    public string? MethodRow { get; init; } = "sp3.tB_1m.undergroundParking";

    /// <summary>Показатель строки табл. Б.1: машино-мест, м² или человек.</summary>
    public double Indicator { get; init; }

    /// <summary>ID строки табл. В.1 с фоновым шумом; null — автостоянка (70 дБА).</summary>
    public string? BackgroundRow { get; init; } = "sp3.tB_1.parking";

    /// <summary>Фоновый шум по заданию, дБА; задан — важнее строки таблицы.</summary>
    public double? BackgroundDba { get; init; }

    /// <summary>ID моделей оповещателей из каталога; null — первая подходящего вида.</summary>
    public string? Sounder { get; init; }

    public string? Light { get; init; }

    /// <summary>Речевое оповещение: отвод громкоговорителя — акустическая мощность, Вт (из паспорта модели); null — наибольший.</summary>
    public double? SpeakerTap { get; init; }

    /// <summary>Речевое оповещение: ID усилителя (блока речевого оповещения) из каталога; null — первый.</summary>
    public string? Amplifier { get; init; }
}

/// <summary>Дымовая зона: вытяжная система, подпор (может отсутствовать), число клапанов дымоудаления.</summary>
public sealed class ProjectSmokeZone
{
    public string Name { get; init; } = "";

    public string ExhaustSystem { get; init; } = "";

    public string? SupplySystem { get; init; }

    public int SmokeDampers { get; init; }
}

/// <summary>Точка плана в координатах чертежа, мм.</summary>
public sealed class ProjectPoint
{
    public double X { get; init; }

    public double Y { get; init; }
}

/// <summary>Зона раскладки ИП: помещение по имени с чертежа («*» — все помещения), тип ИП, алгоритм, адресность, высота до перекрытия.</summary>
public sealed class ProjectAlarmZone
{
    public string Room { get; init; } = "*";

    public Core.DetectorKind Detector { get; init; } = Core.DetectorKind.Smoke;

    public Core.DetectionAlgorithm Algorithm { get; init; } = Core.DetectionAlgorithm.A;

    public bool Addressable { get; init; } = true;

    /// <summary>Высота до перекрытия, м; null — design.roomHeight.</summary>
    public double? CeilingHeight { get; init; }

    /// <summary>ID модели извещателя из data/catalog/alarm_detectors.json; null — первая модель нужного типа (с замечанием).</summary>
    public string? Model { get; init; }

    /// <summary>Имя уровня из alarm.levels, к которому относится зона; null — ко всем уровням.</summary>
    public string? Level { get; init; }
}

/// <summary>Порошковое пожаротушение: модель модуля из data/catalog/powder_modules.json и защищаемые помещения — решение проектировщика.</summary>
public sealed class ProjectPowder
{
    /// <summary>ID модуля из каталога; null — первый в каталоге (с замечанием).</summary>
    public string? Module { get; init; }

    public IReadOnlyList<ProjectPowderRoom> Rooms { get; init; } = [];
}

/// <summary>Защищаемое помещение АУПП: имя с чертежа, способ, класс пожара, горючее вещество (строка табл. И.1), высоты, доля проёмов.</summary>
public sealed class ProjectPowderRoom
{
    public string Room { get; init; } = "";

    public Core.PowderMethod Method { get; init; } = Core.PowderMethod.Area;

    /// <summary>Класс пожара для выбора защищаемой площади модуля: "A" или "B".</summary>
    public string FireClass { get; init; } = "B";

    /// <summary>ID строки табл. И.1 (data/norms/sp485/table_I_1.json), например sp485.tI_1.petrol; null — задать k3.</summary>
    public string? Substance { get; init; }

    public double? K3 { get; init; }

    /// <summary>k1 по решению проектировщика (1…1,2); null — из паспорта или 1,2 на предельной высоте.</summary>
    public double? K1 { get; init; }

    /// <summary>Высота помещения, м — для объёма.</summary>
    public double CeilingHeight { get; init; }

    /// <summary>Высота установки модулей, м; null — высота помещения.</summary>
    public double? MountHeight { get; init; }

    /// <summary>Доля постоянно открытых проёмов в общей поверхности помещения (И.2).</summary>
    public double OpenFraction { get; init; }

    public bool ManualOnly { get; init; }

    /// <summary>Затенённая площадь Sз, м², для k2 по формуле паспорта модуля; 0 — затенений нет (решение проектировщика), null — не задана.</summary>
    public double? ShadedArea { get; init; }
}

/// <summary>Газовое пожаротушение объёмным способом (СП 485 разд. 9, прил. Г, Д, Ж): высота объекта над уровнем моря и помещения.</summary>
public sealed class ProjectGas
{
    /// <summary>Высота объекта над уровнем моря, м — поправка K3 по табл. Г.17.</summary>
    public double Altitude { get; init; }

    /// <summary>Установка модульная (true) или централизованная (false) — нормативное время подачи по 9.7.4.</summary>
    public bool Modular { get; init; } = true;

    public IReadOnlyList<ProjectGasRoom> Rooms { get; init; } = [];
}

/// <summary>Помещение АУГП — исходные данные проектировщика (СП 485, 9.5).</summary>
public sealed class ProjectGasRoom
{
    /// <summary>Имя помещения с чертежа (слой ПТ_КОНТУР).</summary>
    public string Room { get; init; } = "";

    /// <summary>ГОТВ — ключ записи табл. Г (hfc227ea, hfc125, hfc23, co2, nitrogen, argon, inergen, argonite, fk5112, sf6, hfc218, hfc318c, tfm18i, hfc217j1).</summary>
    public string Agent { get; init; } = "hfc227ea";

    /// <summary>Горючий материал — ключ концентрации (heptane, ethanol, petrolA76, machineOil, acetone, toluene, …); не-ЛВЖ и А2 — heptane.</summary>
    public string Material { get; init; } = "heptane";

    public double CeilingHeight { get; init; }

    /// <summary>Суммарная площадь постоянно открытых проёмов, м².</summary>
    public double OpenArea { get; init; }

    public Core.GasOpenings Openings { get; init; } = Core.GasOpenings.Other;

    /// <summary>Минимальная температура воздуха в помещении, °С.</summary>
    public double MinTemperature { get; init; } = 20;

    public Core.GasClassA1 ClassA1 { get; init; } = Core.GasClassA1.None;

    /// <summary>Время подачи по гидравлическому расчёту, с; null — нормативное (9.7.4).</summary>
    public double? DischargeTime { get; init; }

    /// <summary>Остаток ГОТВ в трубопроводах по гидравлическому расчёту, кг; null — не учтён (замечание).</summary>
    public double? PipeResidue { get; init; }

    /// <summary>Остаток в одном модуле по ТД, кг, и число модулей.</summary>
    public double ModuleResidue { get; init; }

    public int Modules { get; init; }

    /// <summary>Предельно допустимое избыточное давление, МПа (ГОСТ Р 12.3.047); null — проём сброса не считается.</summary>
    public double? MaxOverpressure { get; init; }

    /// <summary>Наибольшее число людей в помещении (для CO₂ — 9.1.2).</summary>
    public int People { get; init; }
}
