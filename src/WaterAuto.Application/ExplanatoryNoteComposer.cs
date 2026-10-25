using System.Globalization;
using WaterAuto.Calc.Layout;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application;

/// <summary>Исходные данные пояснительной записки: результаты этапов расчёта и выбранная литература.</summary>
public sealed record NoteInputs(
    NoteTitle Title,
    string ObjectName,
    string RoomGroup,
    ConstructionClass Construction,
    FloorPlan Plan,
    LayoutRules Rules,
    SprinklerLayout Layout,
    RoutingResult Routing,
    MaterialTakeoff Takeoff,
    IReadOnlyList<LiteratureEntry> Literature,
    IReadOnlyList<string> LiteratureIds);

/// <summary>
/// Собирает пояснительную записку из результатов расчёта: числа — только из данных норм и из расчёта.
/// Разделы, которые зависят от нерешённых вопросов по норме (гидравлика, насос, запас воды), выходят
/// выделенными заготовками со ссылками на вопросы — без выдуманных значений.
/// </summary>
public static class ExplanatoryNoteComposer
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static NoteDocument Compose(NoteInputs i, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(i);
        ArgumentNullException.ThrowIfNull(norms);
        var references = Bibliography.Order(i.Literature.Where(e => i.LiteratureIds.Contains(e.Id))).Select(Bibliography.Format).ToList();
        return new NoteDocument(
            i.Title,
            [
                Introduction(i),
                Initial(i),
                Parameters(i, norms),
                Placement(i),
                Routing(i),
                new NoteSection("5", "Гидравлический расчёт", [
                    new NoteParagraph("Расчёт выполняется по приложению Б СП 485: от диктующего оросителя к источнику, с учётом геометрии сети и потерь в трубопроводах."),
                    new NotePending("Раздел формируется после решения вопросов: В-01 — форма диктующей площади; В-02 — к каким трубопроводам относить 20 % местных потерь; В-03 — единицы формулы Б.32 (узел управления). См. docs/open_questions.md."),
                ]),
                new NoteSection("6", "Насосная установка", [
                    new NotePending("Требуемый напор по Б.30 и подбор насоса по характеристике — после гидравлического расчёта и решения вопросов В-02, В-03, В-07 (жокей-насос), В-13 (перевод МПа ↔ м вод. ст.)."),
                ]),
                new NoteSection("7", "Запас воды", [
                    new NotePending(string.Create(Ru, $"Объём по расчётному расходу и продолжительности подачи {TableValue(norms, i.Rules, "t_min")} мин (табл. 6.1) — после гидравлического расчёта.")),
                ]),
                Specification(i),
                new NoteSection("", "Заключение", [
                    new NotePending("Заключение формируется после гидравлического расчёта и подбора насоса."),
                    new NoteParagraph("Результаты, полученные программой Water_Auto, подлежат проверке специалистом по противопожарным системам перед выпуском."),
                ]),
            ],
            references);
    }

    private static NoteSection Introduction(NoteInputs i) => new("", "Введение", [
        new NoteParagraph($"В работе разработана автоматическая установка водяного пожаротушения объекта «{i.ObjectName}» по СП 485.1311500.2020."),
        new NoteParagraph("Размещение оросителей, трассировка трубопроводов и спецификация получены программой Water_Auto из плана этажа; нормативные значения взяты из данных норм с указанием пунктов."),
    ]);

    private static NoteSection Initial(NoteInputs i)
    {
        var rooms = i.Plan.Rooms.Select(r => string.Create(Ru, $"помещение «{r.Name}» — площадь {r.Contour.AreaMm2 / 1e6:0.0} м²")).ToList();
        return new NoteSection("1", "Исходные данные", [
            new NoteList([
                $"объект — {i.ObjectName}",
                $"группа помещений по приложению А СП 485 — {i.RoomGroup}",
                $"класс пожарной опасности перекрытий и стен — {Label(i.Construction)}",
                .. rooms,
                $"колонн на плане — {i.Plan.Columns.Count}, зон без оросителей — {i.Plan.NoSprinklerZones.Count}",
                $"точек подключения (вводов) — {i.Plan.Inlets.Count}",
            ]),
        ]);
    }

    private static NoteSection Parameters(NoteInputs i, INormsProvider norms)
    {
        var table = norms.Find(i.Rules.NormIds[0]);
        string V(string key, string unit) => table?.Values.GetValueOrDefault(key) is { } v ? string.Create(Ru, $"{v:0.###} {unit}") : "—";
        var tableSpacing = table?.Values.GetValueOrDefault("maxSpacing");
        var spacing = tableSpacing is { } ts && Math.Abs(ts - i.Rules.MaxSpacing.Meters) > 1e-9
            ? string.Create(Ru, $"{i.Rules.MaxSpacing.Meters:0.###} м (строже табл. 6.1: {ts:0.###} м)")
            : string.Create(Ru, $"{i.Rules.MaxSpacing.Meters:0.###} м");
        return new NoteSection("2", "Параметры установки", [
            new NoteParagraph($"Параметры приняты для группы помещений {i.RoomGroup} по таблице 6.1 СП 485, расстояния — по п. 6.2.21."),
            new NoteTable("Таблица 1 — Параметры спринклерной установки", ["Параметр", "Значение", "Основание"], [
                ["Интенсивность орошения", V("i_water", "л/(с·м²)"), "табл. 6.1"],
                ["Расход, не менее", V("Q_water", "л/с"), "табл. 6.1"],
                ["Минимальная площадь срабатывания", V("S", "м²"), "табл. 6.1"],
                ["Продолжительность подачи", V("t_min", "мин"), "табл. 6.1"],
                ["Наибольшее расстояние между оросителями", spacing, "табл. 6.1"],
                ["Наименьшее расстояние между оросителями", string.Create(Ru, $"{i.Rules.MinSpacing.Meters:0.###} м"), "п. 6.2.21"],
                ["Наибольшее расстояние до стен", string.Create(Ru, $"{i.Rules.MaxWallDistance.Meters:0.###} м"), "п. 6.2.21"],
            ]),
        ]);
    }

    private static NoteSection Placement(NoteInputs i)
    {
        var rows = i.Plan.Rooms.Select(r =>
        {
            var n = i.Layout.Sprinklers.Count(s => s.RoomId == r.Id);
            var area = r.Contour.AreaMm2 / 1e6;
            return (IReadOnlyList<string>)[r.Name, string.Create(Ru, $"{area:0.0}"), n.ToString(Ru), n > 0 ? string.Create(Ru, $"{area / n:0.0}") : "—"];
        }).ToList();
        return new NoteSection("3", "Размещение оросителей", [
            new NoteParagraph("Помещение делится на прямоугольные зоны; линии оросителей подбираются так, чтобы расстояние между оросителями не превышало нормы и было не меньше наименьшего, а до стен — не больше предела п. 6.2.21. Колонны обходятся сдвигом линий. Из двух направлений деления выбирается вариант с меньшим числом оросителей."),
            new NoteParagraph($"Всего установлено оросителей — {i.Layout.Sprinklers.Count} шт., зон раскладки — {i.Layout.Zones.Count}."),
            new NoteTable("Таблица 2 — Оросители по помещениям", ["Помещение", "Площадь, м²", "Оросителей, шт.", "Площадь на ороситель, м²"], rows),
            .. Remarks(i.Layout.Issues),
        ]);
    }

    private static NoteSection Routing(NoteInputs i)
    {
        var segments = i.Routing.Routes.SelectMany(r => r.Segments).ToList();
        var rows = segments
            .GroupBy(s => (s.Role, s.Dn))
            .OrderBy(g => g.Key.Role)
            .ThenByDescending(g => g.Key.Dn)
            .Select(g => (IReadOnlyList<string>)[Role(g.Key.Role), $"DN{g.Key.Dn}", g.Count().ToString(Ru), string.Create(Ru, $"{g.Sum(s => s.Length.Meters):0.00}")])
            .ToList();
        return new NoteSection("4", "Трассировка трубопроводов", [
            new NoteParagraph("Распределительные трубопроводы (рядки) проложены вдоль рядов оросителей, питающий — поперёк рядов, подводящий — от точки подключения. Начальные диаметры приняты ориентировочно по таблице Б.4 СП 485 по числу оросителей за участком и уточняются гидравлическим расчётом (п. Б.1.3.2)."),
            new NoteTable("Таблица 3 — Трубопроводы по назначению и диаметрам", ["Назначение", "Диаметр", "Участков", "Длина, м"], rows),
            .. Remarks(i.Routing.Issues),
        ]);
    }

    private static NoteSection Specification(NoteInputs i)
    {
        var rows = i.Takeoff.Items
            .Where(t => t.Group != TakeoffGroup.Fittings)
            .Select(t => (IReadOnlyList<string>)[t.Name, string.Create(Ru, $"{t.Quantity:0.##}"), t.Unit, t.Note ?? ""])
            .ToList();
        return new NoteSection("8", "Спецификация", [
            new NoteParagraph("Полная спецификация оборудования, изделий и материалов приведена в документе ПТ.СО (форма 1 ГОСТ 21.110). Основные позиции:"),
            new NoteTable("Таблица 4 — Основные позиции спецификации", ["Наименование", "Количество", "Ед. изм.", "Примечание"], rows),
            new NoteParagraph("Количество фасонных частей определено по трассе ориентировочно и уточняется при разработке узлов."),
        ]);
    }

    private static IEnumerable<NoteBlock> Remarks(IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0)
        {
            yield return new NoteParagraph("Замечаний программы нет.");
            yield break;
        }

        yield return new NoteParagraph("Замечания программы, требующие решения проектировщика:");
        yield return new NoteList(issues.Select(x => x.Clause is null ? x.Message : $"{x.Message} (п. {x.Clause})").ToList());
    }

    private static string? TableValue(INormsProvider norms, LayoutRules rules, string key) =>
        norms.Find(rules.NormIds[0])?.Values.GetValueOrDefault(key) is { } v ? v.ToString("0.###", Ru) : "[—]";

    private static string Role(RouteSegmentRole role) => role switch
    {
        RouteSegmentRole.Supply => "подводящий",
        RouteSegmentRole.Main => "питающий",
        _ => "распределительный",
    };

    private static string Label(ConstructionClass c) => c switch
    {
        ConstructionClass.K0 => "К0",
        ConstructionClass.K1 => "К1",
        ConstructionClass.K2 => "К2",
        ConstructionClass.K3 => "К3",
        _ => "не нормируется",
    };
}
