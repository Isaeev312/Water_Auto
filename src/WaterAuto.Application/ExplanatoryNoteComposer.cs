using System.Globalization;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
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
    DesignResult Design,
    DesignSettings DesignSettings,
    IReadOnlyList<LiteratureEntry> Literature,
    IReadOnlyList<string> LiteratureIds,
    SprinklerSelectionResult? Selection = null,
    string? Joints = null,
    PipeSupportsResult? Supports = null,
    TemperatureChoice? Temperature = null,
    double? MaxAmbient = null,
    int HydrantCount = 0,
    Length? HydrantReach = null);

/// <summary>
/// Собирает пояснительную записку из результатов расчёта: числа — только из данных норм и из расчёта.
/// Где СП 485 допускает разные прочтения (В-01, В-02, В-03, В-08), записка прямо называет принятое — строгое —
/// прочтение со ссылкой на пункт; сами вопросы остаются открытыми в docs/open_questions.md.
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
                Routing(i, norms),
                Hydraulics(i, norms),
                Pump(i, norms),
                Water(i, norms),
                Specification(i),
                Interfaces(),
                Operation(i, norms),
                ControlValve(norms),
                Installation(i, norms),
                Maintenance(norms),
                Conclusion(i),
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
        var p = i.Design.Parameters;
        var table = norms.Find(i.Rules.NormIds[0]);
        var tableSpacing = table?.Values.GetValueOrDefault("maxSpacing");
        var spacing = tableSpacing is { } ts && Math.Abs(ts - i.Rules.MaxSpacing.Meters) > 1e-9
            ? string.Create(Ru, $"{i.Rules.MaxSpacing.Meters:0.###} м (строже табл. 6.1: {ts:0.###} м)")
            : string.Create(Ru, $"{i.Rules.MaxSpacing.Meters:0.###} м");
        var heights = new List<string>();
        if (p.RoomHeight is { } rh)
        {
            heights.Add(string.Create(Ru, $"высота помещения — {rh.Meters:0.##} м"));
        }

        if (p.StorageHeight is { } sh)
        {
            heights.Add(string.Create(Ru, $"высота складирования — {sh.Meters:0.##} м"));
        }

        var blocks = new List<NoteBlock>
        {
            new NoteParagraph($"Параметры приняты для группы помещений {i.RoomGroup} по {p.Source} СП 485 (п. 6.1.4), расстояния — по п. 6.2.21{(heights.Count > 0 ? "; " + string.Join(", ", heights) : "")}."),
        };
        if (Math.Abs(p.HeightFactor - 1) > 1e-12 && p.RoomHeight is { } h)
        {
            var baseI = p.Intensity / p.HeightFactor;
            var baseQ = p.Flow.LitersPerSecond / p.HeightFactor;
            blocks.Add(new NoteParagraph("Высота помещения склада более 10 м — интенсивность и расход по примечанию 3 к таблице 6.2:"));
            blocks.Add(new NoteFormula(string.Create(Ru, $"*i*_{{h}} = [1 + 0,05·(*H* − 10)]·*i* = [1 + 0,05·({h.Meters:0.##} − 10)]·{baseI:0.###} = {p.Intensity:0.###} л/(с·м^{{2}}),"), "2.1", []));
            blocks.Add(new NoteFormula(string.Create(Ru, $"*Q*_{{h}} = [1 + 0,05·(*H* − 10)]·*Q* = {p.HeightFactor:0.###}·{baseQ:0.##} = {p.Flow.LitersPerSecond:0.##} л/с,"), "2.2", [
                "где *i*, *Q* — по таблице 6.2 при высоте помещения не более 10 м; *H* — высота помещения, м.",
            ]));
        }

        blocks.Add(new NoteTable("Таблица 1 — Параметры спринклерной установки", ["Параметр", "Значение", "Основание"], [
            ["Интенсивность орошения", string.Create(Ru, $"{p.Intensity:0.###} л/(с·м²)"), p.Source],
            ["Расход, не менее", string.Create(Ru, $"{p.Flow.LitersPerSecond:0.##} л/с"), p.Source],
            ["Минимальная площадь срабатывания", string.Create(Ru, $"{p.MinArea:0.###} м²"), p.Source == "табл. 6.3" ? "табл. 6.3" : "табл. 6.1"],
            ["Продолжительность подачи", string.Create(Ru, $"{p.Duration:0.###} мин"), "табл. 6.1"],
            ["Наибольшее расстояние между оросителями", spacing, "табл. 6.1"],
            ["Наименьшее расстояние между оросителями", string.Create(Ru, $"{i.Rules.MinSpacing.Meters:0.###} м"), "п. 6.2.21"],
            ["Наибольшее расстояние до стен", string.Create(Ru, $"{i.Rules.MaxWallDistance.Meters:0.###} м"), "п. 6.2.21"],
        ]));
        if (i.Temperature is { } temp)
        {
            blocks.Add(new NoteParagraph(string.Create(Ru, $"**Температура срабатывания.** Максимальная температура среды в зоне расположения оросителей — {i.MaxAmbient:0.#} °С (п. 6.2.17). По таблице 6.4 СП 485 (п. 6.2.16) принята номинальная температура срабатывания **{temp.Nominal} °С**: предельно допустимая рабочая температура среды для неё — не более {temp.MaxAmbient} °С. Маркировочный цвет жидкости в термоколбе — {temp.Color}, номинальное время срабатывания — не более {temp.ResponseTime} с (ГОСТ Р 51043-2002, табл. 2).")));
        }

        if (i.Selection is { Chosen: { } chosen } sel)
        {
            blocks.Add(new NoteParagraph(string.Create(Ru, $"**Подбор оросителя.** Ороситель подобран по каталогу производителя: для каждой модели выполнен полный гидравлический расчёт. Модель допускается, если давление у оросителей диктующей зоны лежит в её рабочем диапазоне, средняя интенсивность по каталогу при давлении у диктующего оросителя не меньше нормативной ({i.Design.Parameters.Intensity:0.###} л/(с·м²)) и защищаемая площадь не меньше площади на ороситель. Из допустимых принята модель с наименьшим произведением расхода и давления насоса: **{chosen.Maker} {chosen.Model}**, *K* = {chosen.K:0.###} л/(с·МПа^{{0,5}}). Данные каталога подлежат сверке с паспортом изделия.")));
            blocks.Add(new NoteTable("Таблица 6 — Подбор оросителя по каталогу", ["Модель", "K", "Q_нас, л/с", "P_нас, МПа", "Результат"],
                sel.Rows.GroupBy(r => r.Candidate.K).Select(g => g.FirstOrDefault(r => r.Candidate.Id == chosen.Id) ?? g.First()).OrderBy(r => r.Candidate.K)
                    .Select(r => (IReadOnlyList<string>)[r.Candidate.Model, string.Create(Ru, $"{r.Candidate.K:0.###}"), string.Create(Ru, $"{r.PumpFlow.LitersPerSecond:0.00}"), string.Create(Ru, $"{r.PumpPressure.Mpa:0.000}"), r.Candidate.Id == chosen.Id ? "принят" : r.Reason])
                    .ToList()));
        }

        return new NoteSection("2", "Параметры установки", blocks);
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
            .. Beams(i),
            .. Ducts(i),
            new NoteTable("Таблица 2 — Оросители по помещениям", ["Помещение", "Площадь, м²", "Оросителей, шт.", "Площадь на ороситель, м²"], rows),
            .. Remarks(i.Layout.Issues),
        ]);
    }

    private static IEnumerable<NoteBlock> Beams(NoteInputs i)
    {
        var high = i.Plan.Beams.Count(b => b.Height.Meters > i.Rules.BeamHeightLimit.Meters + 1e-9);
        if (high == 0)
        {
            yield break;
        }

        yield return new NoteParagraph(string.Create(Ru, $"*Принято:* балки высотой более {i.Rules.BeamHeightLimit.Meters:0.##} м ({high} шт.) делят перекрытие на поля, оросители размещаются между балками (п. 6.2.10). У граней балок расстояние до грани — не более предела п. 6.2.21, а расстояние между оросителями через балку — не более нормативного; трубопроводы проходят под балками."));
    }

    private static IEnumerable<NoteBlock> Ducts(NoteInputs i)
    {
        var under = i.Layout.Sprinklers.Where(s => s.UnderObstacleId is not null).ToList();
        if (under.Count == 0)
        {
            yield break;
        }

        var ducts = under.Select(s => s.UnderObstacleId).Distinct().Count();
        yield return new NoteParagraph(string.Create(Ru, $"Под воздуховодами (площадками, оборудованием) шириной более {i.Rules.DuctMinWidth.Meters:0.##} м ({ducts} шт.) дополнительно установлено **{under.Count}** оросителей (п. 6.2.14); расстояние от термочувствительного элемента до плоскости воздуховода — по п. 6.2.11. *Принято:* такие воздуховоды препятствуют орошению защищаемой поверхности."));
    }

    // Крепление трубопроводов: шаг опор по табл. 6.7.1, держатель у последнего оросителя, стыки у опор.
    private static List<NoteBlock> SupportBlocks(NoteInputs i, INormsProvider norms)
    {
        if (i.Supports is not { } s || s.Points.Count == 0)
        {
            return [];
        }

        var last = norms.Find("sp485.p6_7_1_26.lastSprinkler")!.Values;
        var joints = norms.Find("sp485.p6_7_1_31.joints")!.Values;

        return
        [
            new NoteParagraph($"**Крепление трубопроводов.** Трубопроводы крепятся к конструкциям здания держателями из негорючих материалов (пп. 6.7.1.9, 6.7.1.13 СП 485). Расстояние между опорами стальных трубопроводов принято по таблице 6.7.1 по наружному диаметру трубы (для диаметра, которого нет в таблице, — по ближайшему меньшему, в запас); расстояние от держателя до последнего оросителя — не более {F(last["smallDistance"]!.Value)} м для DN {F(last["smallDnMax"]!.Value)} и менее и {F(last["largeDistance"]!.Value)} м для большего (п. 6.7.1.26); сварные стыки — не ближе {F(joints["weldFromSupport"]!.Value)} мм, соединения — не ближе {F(joints["jointFromSupport"]!.Value)} мм от опор (п. 6.7.1.31). Тип опор (подвижные, неподвижные) назначается по условию компенсации температурных удлинений (п. 6.7.1.10)."),
            new NoteTable("Таблица 7 — Опоры трубопроводов", ["Диаметр", "Шаг опор, м, не более", "Количество опор, шт."],
                s.Points.GroupBy(p => p.Dn).OrderBy(g => g.Key)
                    .Select(g => (IReadOnlyList<string>)[$"DN{g.Key}", string.Create(Ru, $"{s.MaxSpacing(g.Key):0.0#}"), g.Count().ToString(Ru)])
                    .Append([ "Итого", "", s.Points.Count.ToString(Ru) ])
                    .ToList()),
        ];
    }

    private static NoteSection Routing(NoteInputs i, INormsProvider norms)
    {
        var segments = i.Routing.Routes.SelectMany(r => r.Segments.Concat(r.Loops)).ToList();
        var rows = segments
            .GroupBy(s => (s.Role, s.Dn))
            .OrderBy(g => g.Key.Role)
            .ThenByDescending(g => g.Key.Dn)
            .Select(g => (IReadOnlyList<string>)[Role(g.Key.Role), $"DN{g.Key.Dn}", g.Count().ToString(Ru), string.Create(Ru, $"{g.Sum(s => s.Length.Meters):0.00}")])
            .ToList();
        return new NoteSection("4", "Трассировка трубопроводов", [
            new NoteParagraph("Распределительные трубопроводы (рядки) проложены вдоль рядов оросителей, питающий — поперёк рядов, подводящий — от точки подключения. Начальные диаметры приняты ориентировочно по таблице Б.4 СП 485 по числу оросителей за участком и уточняются гидравлическим расчётом (п. Б.1.3.2)."),
            .. (i.Joints is { } joints ? [new NoteParagraph(joints)] : Array.Empty<NoteBlock>()),
            new NoteTable("Таблица 3 — Трубопроводы по назначению и диаметрам", ["Назначение", "Диаметр", "Участков", "Длина, м"], rows),
            .. SupportBlocks(i, norms),
            .. Remarks(i.Routing.Issues),
        ]);
    }

    /// <summary>Ссылка на норму: «п. 6.2.21», но «табл. Б.4» без «п.».</summary>
    public static string ClauseRef(string clause) =>
        clause.StartsWith("табл.", StringComparison.Ordinal) || clause.StartsWith("прил.", StringComparison.Ordinal) ? clause : $"п. {clause}";

    private static string F(double v, string format = "0.###") => v.ToString(format, Ru);

    private static NoteSection Hydraulics(NoteInputs i, INormsProvider norms)
    {
        var d = i.Design;
        var s = i.DesignSettings;
        var intensity = d.Parameters.Intensity;
        var minArea = d.Parameters.MinArea;
        var blocks = new List<NoteBlock>
        {
            new NoteParagraph("Гидравлический расчёт выполнен по приложению Б СП 485.1311500.2020 (п. Б.1.1.12) последовательно от диктующего оросителя к узлу подключения."),
            new NoteParagraph("*Принято:*"),
            new NoteList([
                "диктующий ороситель — тот из оросителей в концах ветвей, при расчётной площади у которого требуемое давление насоса наибольшее (проверены все концы ветвей, п. Б.1.2.1);",
                $"диктующая зона — {d.ZoneSprinklers.Count} оросителей, ближайших к диктующему, площадью не менее минимальной площади орошения ({d.Parameters.Source}) (пп. Б.1.2.1, Б.1.2.2);",
                $"расход диктующего оросителя при отсутствии эпюр орошения — по формуле (Б.1) с верхним значением коэффициента {F(d.DictatingFactor)};",
                $"местные сопротивления, включая потери в узле управления, — {F(d.LocalLossFactor * 100, "0")} % линейных потерь на всех участках от диктующего оросителя до насоса (п. Б.1.3.13);",
                s.DesignVelocity is { } v
                    ? $"диаметры участков — по формуле (Б.5) при скорости *V* = {F(v)} м/с и коэффициенте расхода μ = {F(d.DischargeCoefficient, "0.00")} (п. Б.1.2.6);"
                    : "диаметры участков — ориентировочно по таблице Б.4 (п. Б.1.3.2);",
                $"трубы — стальные по {s.PipeStandard}, удельные характеристики *K*_{{т}} — по таблице Б.2.",
            ]),
            new NoteParagraph("**5.1 Расход и давление у диктующего оросителя**"),
            new NoteParagraph("Расход диктующего оросителя определяется по формуле (Б.1):"),
            new NoteFormula($"*q*_{{1}} = {F(d.DictatingFactor)}·*i*·*s* = {F(d.DictatingFactor)}·{F(intensity, "0.##")}·12 = {F(d.DictatingFlow.LitersPerSecond, "0.00")} л/с,", "5.1", [
                $"где *i* — нормативная интенсивность орошения ({d.Parameters.Source}), л/(с·м^{{2}});",
                "*s* — круговая площадь, защищаемая диктующим оросителем, *s* = 12 м^{2} (п. Б.1.1.10).",
            ]),
            new NoteParagraph("Давление у диктующего оросителя:"),
            new NoteFormula($"*p*_{{1}} = (*q*_{{1}} / 10*K*)^{{2}} = ({F(d.DictatingFlow.LitersPerSecond, "0.00")} / (10·{F(s.SprinklerK.Value)}))^{{2}} = {F(d.DictatingPressure.Mpa, "0.000")} МПа,", "5.2", [
                $"где *K* — коэффициент производительности оросителя, *K* = {F(s.SprinklerK.Value)} л/(с·МПа^{{0,5}}) (по технической документации).",
            ]),
            new NoteParagraph("**5.2 Количество оросителей в диктующей зоне**"),
            new NoteParagraph("Условная расчётная площадь на один ороситель по сетке распределительной сети (Б.3):"),
            new NoteFormula($"*Q*_{{ор}} = *l*·*b* = {F(d.RowSpacing.Meters, "0.00")}·{F(d.LineSpacing.Meters, "0.00")} = {F(d.AreaPerSprinkler, "0.00")} м^{{2}},", "5.3", [
                "где *l* — расстояние между оросителями в рядке, м;",
                "*b* — расстояние между рядками, м.",
            ]),
            new NoteParagraph("Количество оросителей в диктующей зоне (Б.2):"),
            new NoteFormula($"*N* = *S* / *Q*_{{ор}} = {F(minArea, "0")} / {F(d.AreaPerSprinkler, "0.00")} = {F(minArea / d.AreaPerSprinkler, "0.00")} → **{d.ZoneSprinklers.Count} шт.**,", "5.4", [
                $"где *S* — минимальная площадь орошения ({(d.Parameters.Source == "табл. 6.3" ? "табл. 6.3" : "табл. 6.1")}), м^{{2}}.",
            ]),
        };

        if (s.DesignVelocity is { } velocity)
        {
            blocks.Add(new NoteParagraph("**5.3 Диаметры трубопроводов**"));
            blocks.Add(new NoteParagraph("Диаметр участка определяется по формуле (Б.5); для участка 1–2:"));
            blocks.Add(new NoteFormula($"*d* = 1000·√(4·*q*_{{1}} / (π·μ·*V*)) = 1000·√(4·{F(d.DictatingFlow.LitersPerSecond / 1000, "0.00000")} / (3,14·{F(d.DischargeCoefficient, "0.00")}·{F(velocity)})) = {F(d.FirstSegmentDiameterMm, "0.0")} мм,", "5.5", [
                "где μ — коэффициент расхода (0,90–0,95 при отсутствии данных, п. Б.1.2.6);",
                $"*V* — скорость движения воды, м/с (не более {F(d.VelocityLimit)} м/с, п. Б.1.2.6, п. 6.7.1.37).",
            ]));
            blocks.Add(new NoteParagraph($"Принимается ближайшая большая труба сортамента; остальные участки подобраны так же по фактическому расходу, расчёт повторён до установления диаметров. Диаметры участков ближе к источнику не меньше, чем на участках за ними."));
        }

        blocks.Add(new NoteParagraph("**5.4 Потери давления и расход установки**"));
        blocks.Add(new NoteParagraph("Потери давления на участке определяются по формуле (Б.6), с учётом местных сопротивлений (п. Б.1.3.13):"));
        blocks.Add(new NoteFormula($"Δ*P* = *Q*^{{2}}·*L* / (100·*K*_{{т}}),   Δ*P*′ = (1 + {F(d.LocalLossFactor, "0.0")})·Δ*P*,", "5.6", [
            "где *Q* — расход на участке, л/с;",
            "*L* — длина участка, м;",
            "*K*_{т} — удельная характеристика трубопровода по таблице Б.2, л^{2}/с^{2}.",
        ]));
        blocks.Add(new NoteParagraph("Давление и расход каждого следующего оросителя: *p*_{n} = *p*_{n−1} + Δ*P*′, *q*_{n} = 10*K*·√*p*_{n} (Б.4); расходы рядков — по их характеристикам (Б.17–Б.21). Результаты по расчётному направлению — в таблице 5."));

        // Расчётные точки, как в приложении Б: оросители — 1, 2, 3…, узлы — а, б, в…, точка подключения — ВВ.
        var kinds = d.Route.Nodes.ToDictionary(n => n.Id, n => n.Kind);
        var labels = new Dictionary<string, string>();
        var (sprinklerNo, letterNo) = (0, 0);
        const string Letters = "абвгдежзиклмнопрстуфхцчшэюя";
        string Label(string id) => labels.TryGetValue(id, out var l) ? l
            : labels[id] = kinds.GetValueOrDefault(id) switch
            {
                RouteNodeKind.Sprinkler => (++sprinklerNo).ToString(Ru),
                RouteNodeKind.Inlet => "ВВ",
                _ => letterNo < Letters.Length ? Letters[letterNo++].ToString() : $"у{++letterNo}",
            };
        // При ВПВ, требующем большего давления, сеть рассчитана от поднятого давления диктующего оросителя (ADR 0010).
        var pStart = d.Hydrants is { Raised: true } raised ? raised.DictatingPressureActual.Mpa : d.DictatingPressure.Mpa;
        var pressure = pStart;
        var rows = new List<IReadOnlyList<string>>();
        foreach (var x in d.Path.Reverse())
        {
            pressure += x.LossWithLocal.Mpa;
            rows.Add([
                $"{Label(x.To)}–{Label(x.From)}",
                $"DN{x.Dn}",
                F(x.Length.Meters, "0.00"),
                F(x.Kt.Kt, "0.##"),
                F(x.Flow.LitersPerSecond, "0.00"),
                F(x.LinearLoss.Mpa, "0.0000"),
                F(x.LossWithLocal.Mpa, "0.0000"),
                F(pressure, "0.000"),
                F(x.Velocity, "0.00"),
            ]);
        }

        blocks.Add(new NoteTable("Таблица 5 — Гидравлический расчёт по расчётному направлению (от диктующего оросителя 1 к точке подключения ВВ)",
            ["Участок", "DN", "L, м", "Kт, л²/с²", "Q, л/с", "ΔP, МПа", "ΔP′, МПа", "P в конце, МПа", "v, м/с"], rows));
        blocks.Add(new NoteParagraph("Обозначения расчётных точек: 1, 2, 3… — оросители по ходу от диктующего; а, б, в… — узлы ответвлений; ВВ — точка подключения (ПТ_ВВОД). *P в конце* — давление в конце участка по ходу к источнику: *p*_{1} плюс сумма Δ*P*′."));

        blocks.Add(new NoteParagraph("Расчётный расход установки — сумма расходов оросителей диктующей зоны (Б.23):"));
        blocks.Add(new NoteFormula($"*Q*_{{р}} = Σ*q*_{{n}} = **{F(d.DesignFlow.LitersPerSecond, "0.00")} л/с**,", "5.7", [
            $"где *n* — количество оросителей в диктующей зоне, *n* = {d.ZoneSprinklers.Count}.",
        ]));
        var qAup = d.PumpFlow.LitersPerSecond - (d.Hydrants?.TotalFlow.LitersPerSecond ?? 0);
        var aupName = d.Hydrants is null ? "*Q*_{нас}" : "*Q*_{АУП}";
        blocks.Add(new NoteParagraph(d.PumpFlowClause.StartsWith("Б.24", StringComparison.Ordinal)
            ? $"Так как *Q*_{{р}} = {F(d.DesignFlow.LitersPerSecond, "0.00")} л/с ≤ *Q*_{{н}} = {F(d.NormativeFlow.LitersPerSecond)} л/с (Б.24), установка должна обеспечивать нормативный расход: {aupName} = **{F(qAup, "0.00")} л/с**."
            : $"Так как *Q*_{{р}} = {F(d.DesignFlow.LitersPerSecond, "0.00")} л/с > *Q*_{{н}} = {F(d.NormativeFlow.LitersPerSecond)} л/с (Б.25), расход принимается не менее расчётного (Б.26): {aupName} = **{F(qAup, "0.00")} л/с**."));
        blocks.Add(new NoteParagraph("Давление в точке подключения распределительной сети:"));
        if (d.Hydrants is { Raised: true })
        {
            blocks.Add(new NoteParagraph($"Давление у диктующего оросителя принято {F(pStart, "0.000")} МПа — больше значения по (Б.1), так как его требуют пожарные краны совмещённой сети (см. ниже, формула 5.10); расходы оросителей в таблице — при этом давлении."));
        }

        blocks.Add(new NoteFormula($"*P*_{{вв}} = *p*_{{1}} + ΣΔ*P*′ = {F(pStart, "0.000")} + {F(d.Path.Sum(x => x.LossWithLocal.Mpa), "0.000")} = **{F(d.InletNodePressure.Mpa, "0.000")} МПа**.", "5.8", []));
        if (d.Ring is { } ring)
        {
            blocks.Add(new NoteParagraph($"**Кольцевая сеть.** Питающий трубопровод закольцован. Расчёт выполнен двумя способами: расчётом сети с кольцом (узловой метод, баланс расходов в узлах) и по п. Б.1.2.16.1 — как тупиковая сеть без замыкающего участка при {F(ring.SharePercent, "0")} % расчётного расхода по полукольцу{(ring.SharePercent >= 100 ? " (на кольце есть нормально закрытые разделительные запорные устройства)" : "")}. Замыкающий участок принят в наиболее удалённой от ввода части кольца."));
            blocks.Add(new NoteFormula($"*P*_{{вв}} = max(*P*_{{кольцо}}; *P*_{{Б.1.2.16.1}}) = max({F(ring.SolverInletPressure.Mpa, "0.000")}; {F(ring.NormativeInletPressure.Mpa, "0.000")}) = **{F(d.InletNodePressure.Mpa, "0.000")} МПа** — принят {(ring.NormativeTaken ? "нормативный вариант" : "расчёт кольца")}.", "5.12", []));
        }

        if (d.Hydrants is { } h)
        {
            blocks.AddRange(HydrantBlocks(d, h, qAup));
            if (i.HydrantCount > 0 && i.HydrantReach is { } reach)
            {
                blocks.Add(new NoteParagraph($"**Размещение пожарных кранов.** Краны размещены у стен так, чтобы каждая точка помещения орошалась {h.Demand.Jets} струями (п. 7.14 СН КР 40-04:2025); струя достаёт точку в пределах радиуса действия {F(reach.Meters, "0.#")} м по прямой внутри помещения — длина рукава {h.Settings.HoseLength} м плюс радиус компактной части струи {F(h.Demand.TableJetHeight.Meters, "0")} м (п. 7.9). Принято **{i.HydrantCount}** пожарных кранов; места кранов — на плане (слой ПТ_ПК), клапаны — на высоте {F(norms.Find("snkr4004.p7_16.valveHeight")?.Values["height"] ?? 0, "0.00")} м от пола (п. 7.16)."));
            }
        }

        var vMax = d.Path.Max(x => x.Velocity);
        blocks.Add(new NoteParagraph(vMax <= d.VelocityLimit
            ? $"Наибольшая скорость на расчётном направлении — {F(vMax, "0.00")} м/с, что не превышает {F(d.VelocityLimit)} м/с для напорных трубопроводов (п. 6.7.1.37)."
            : $"*Внимание:* скорость {F(vMax, "0.00")} м/с больше {F(d.VelocityLimit)} м/с (п. 6.7.1.37) — диаметры требуется увеличить."));
        return new NoteSection("5", "Гидравлический расчёт", blocks);
    }

    // ВПВ, совмещённый с АУП: Ж.2, Ж.3 и п. 7.9 СН КР 40-04:2025; включение ПК в сеть — Б.1.3.8, Б.1.3.16; расход — Б.29 (ADR 0010).
    private static List<NoteBlock> HydrantBlocks(DesignResult d, HydrantResult h, double qAup)
    {
        var s = h.Settings;
        var v = h.Demand;
        var n = v.Jets;
        return
        [
            new NoteParagraph("**Внутренний противопожарный водопровод.** ВПВ совмещён с АУП; совмещение — по пп. Б.1.3.7, Б.1.3.8, Б.1.3.16 СП 485. Пункт Б.1.3.8 отсылает за минимальным расходом крана к СП 10.13130 (табл. 1–2); для объекта в Кыргызской Республике вместо них применены таблицы Ж.2, Ж.3 СН КР 40-04:2025 (решение по проекту, ADR 0010)."),
            new NoteParagraph(s.IsParking || s.Zh1Row is not null
                ? s.Zh1Row is not null && !s.IsParking
                    ? $"По {NoteDocx.Escape(v.Source)} принято **{n}** струи с расходом не менее *q*_{{min}} = {F(v.MinJetFlow.LitersPerSecond)} л/с каждая."
                    : $"Для отапливаемой автостоянки закрытого типа по {NoteDocx.Escape(v.Source)} (объём пожарного отсека {F(s.VolumeThousandM3, "0.#")} тыс. м³) принято **{n}** струи с расходом не менее *q*_{{min}} = {F(v.MinJetFlow.LitersPerSecond)} л/с каждая."
                : $"По {NoteDocx.Escape(v.Source)} (степень огнестойкости {s.FireResistance}, категория {s.Category}, класс {s.HazardClass}, объём {F(s.VolumeThousandM3, "0.#")} тыс. м³, высота здания {F(s.BuildingHeight.Meters, "0.#")} м) принято **{n}** струи с расходом не менее *q*_{{min}} = {F(v.MinJetFlow.LitersPerSecond)} л/с каждая."),
            new NoteParagraph($"Высота компактной части струи — не менее высоты помещения и не менее предела п. 7.9: *H*_{{к}} = {F(v.RequiredJetHeight.Meters, "0.##")} м. По таблице Ж.3 (клапан DN{s.ValveDn}, спрыск {s.NozzleMm} мм, рукав {s.HoseLength} м) принята первая строка с высотой струи не меньше требуемой и расходом ствола не меньше *q*_{{min}} — {F(v.TableJetHeight.Meters, "0")} м, без интерполяции: *q*_{{ПК}} = {F(v.JetFlow.LitersPerSecond)} л/с, *H*_{{ПК}} = {F(v.Head.Meters)} м."),
            new NoteParagraph("Пожарные краны включены в расчётную сеть АУП как потребители с характеристикой:"),
            new NoteFormula($"*K*_{{ПК}} = *q*_{{ПК}} / √(*H*_{{ПК}}/100) = {F(v.JetFlow.LitersPerSecond)} / √{F(v.Head.Meters / 100, "0.000")} = {F(h.K, "0.000")} л/(с·МПа^{{0,5}}).", "5.9", []),
            new NoteParagraph($"*Принято:* {n} крана диктующей секции присоединены в точке подключения трубопроводом DN{h.Pipe.Dn} длиной {F(s.PipeLength.Meters, "0.0")} м (*K*_{{т}} = {F(h.Pipe.Kt.Kt, "0.##")}), по которому идёт весь расход кранов; местные потери — {F(d.LocalLossFactor * 100, "0")} % (п. Б.1.3.13). Клапан диктующего ПК — на {F(s.AboveAxis.Meters, "0.00")} м над осью насоса."),
            new NoteFormula($"*P*_{{вв,ПК}} = *H*_{{ПК}}/100 + Δ*Z* + (1 + {F(d.LocalLossFactor, "0.0")})·(*n*·*q*)^{{2}}·*L* / (100·*K*_{{т}}) = {F(v.Head.Meters / 100, "0.000")} + ({F(h.Elevation.Mpa, "0.000")}) + {F((1 + d.LocalLossFactor) * n * n * v.JetFlow.LitersPerSecond * v.JetFlow.LitersPerSecond * s.PipeLength.Meters / (100 * h.Pipe.Kt.Kt), "0.000")} = {F(h.RequiredNodePressure.Mpa, "0.000")} МПа,", "5.10", [
                "где Δ*Z* — разность отметок клапана ПК и диктующего оросителя, делённая на 100, МПа.",
            ]),
            new NoteParagraph(h.Raised
                ? $"Требуемое давление для кранов больше давления спринклерной сети в точке подключения, поэтому давление сети поднято до *P*_{{вв}} = {F(h.RequiredNodePressure.Mpa, "0.000")} МПа (давление у диктующего оросителя {F(h.DictatingPressureActual.Mpa, "0.000")} МПа); расходы оросителей пересчитаны при этом давлении. Расход крана — *q*_{{ПК}} = {F(h.JetFlowActual.LitersPerSecond, "0.00")} л/с."
                : $"Давление спринклерной сети в точке подключения не меньше требуемого для кранов, поэтому расход крана — фактический при этом давлении (п. Б.1.3.8): *q*_{{ПК}} = **{F(h.JetFlowActual.LitersPerSecond, "0.00")} л/с** ≥ {F(v.JetFlow.LitersPerSecond)} л/с."),
            new NoteParagraph("Расход насосов совмещённого водопровода (Б.29):"),
            new NoteFormula($"*Q*_{{нас}} = *Q*_{{АУП}} + *n*·*q*_{{ПК}} = {F(qAup, "0.00")} + {n}·{F(h.JetFlowActual.LitersPerSecond, "0.00")} = **{F(d.PumpFlow.LitersPerSecond, "0.00")} л/с**.", "5.11", []),
            .. (d.Issues.Any(x => x.Code == "vpv.diaphragm")
                ? [new NoteParagraph($"Давление у пожарного крана *P*_{{ПК}} = (*q*_{{ПК}} / *K*_{{ПК}})^{{2}} = {F(h.ValvePressure.Mpa, "0.000")} МПа больше 0,4 МПа, поэтому между краном и соединительной головкой предусматриваются диафрагмы или регуляторы давления (примечание к п. 7.11 СН КР 40-04:2025). Расход кранов в расчёте принят без учёта диафрагм — с запасом.")]
                : Array.Empty<NoteBlock>()),
            new NoteParagraph("Продолжительность работы ВПВ, совмещённого с АУП, равна времени работы АУП (п. Б.1.3.15 СП 485; п. 7.13 СН КР 40-04:2025)."),
        ];
    }

    private static NoteSection Pump(NoteInputs i, INormsProvider norms)
    {
        var d = i.Design;
        var s = i.DesignSettings;
        var supplyDn = d.SupplyPipe?.Dn ?? s.SupplyDn;
        var kt = d.SupplyPipe?.Kt.Kt ?? 0;
        var m3h = d.PumpFlow.LitersPerSecond * 3.6;
        var jockey = norms.Find("sp485.p6_9_4.jockeyTank");
        return new NoteSection("6", "Насосная установка", [
            new NoteParagraph("Требуемое давление пожарного насоса определяется по формуле (Б.30):"),
            new NoteFormula("*P*_{н} = Δ*P*_{г} + Δ*P*_{в} + ΣΔ*P*_{м} + *P*_{уу} + *p*_{д} + *Z* − *P*_{вх},", "6.1", [
                "где Δ*P*_{г}, Δ*P*_{в} — потери давления на горизонтальных и вертикальных участках трубопровода, МПа;",
                "ΣΔ*P*_{м} — потери в местных сопротивлениях, МПа;",
                "*P*_{уу} — потери в узле управления, МПа;",
                "*p*_{д} — давление у диктующего оросителя, МПа;",
                "*Z* — пьезометрическое давление (геометрическая высота диктующего оросителя над осью насоса), *Z* = *H*/100, МПа;",
                "*P*_{вх} — давление на входе насоса, МПа.",
            ]),
            new NoteParagraph($"*Принято:* потери в местных сопротивлениях и в узле управления — {F(d.LocalLossFactor * 100, "0")} % линейных потерь (п. Б.1.3.13); потери распределительной сети от диктующего оросителя до точки подключения учтены в *P*_{{вв}} (формула 5.8)."),
            new NoteParagraph($"Потери в подводящем трубопроводе от насоса до точки подключения (DN{supplyDn}, *L* = {F(s.SupplyLength.Meters, "0.0")} м, *K*_{{т}} = {F(kt, "0.##")}) при расходе насоса:"),
            new NoteFormula($"Δ*P*_{{подв}} = *Q*_{{нас}}^{{2}}·*L* / (100·*K*_{{т}}) = {F(d.PumpFlow.LitersPerSecond, "0.00")}^{{2}}·{F(s.SupplyLength.Meters, "0.0")} / (100·{F(kt, "0.##")}) = {F(d.SupplyLinearLoss.Mpa, "0.0000")} МПа.", "6.2", []),
            new NoteFormula($"*Z* = *H* / 100 = {F(s.DictatingAboveAxis.Meters, "0.00")} / 100 = {F(d.Elevation.Mpa, "0.000")} МПа.", "6.3", [
                "где *H* — высота диктующего оросителя над осью насоса, м.",
            ]),
            new NoteParagraph("Требуемое давление насоса:"),
            new NoteFormula($"*P*_{{н}} = *P*_{{вв}} + (1 + {F(d.LocalLossFactor, "0.0")})·Δ*P*_{{подв}} + *Z* − *P*_{{вх}} = {F(d.InletNodePressure.Mpa, "0.000")} + {F((1 + d.LocalLossFactor) * d.SupplyLinearLoss.Mpa, "0.000")} + {F(d.Elevation.Mpa, "0.000")} − {F(s.InletPressure.Mpa, "0.000")} = **{F(d.PumpPressure.Mpa, "0.000")} МПа**,", "6.4", [
                $"где *P*_{{вх}} — гарантированное давление в наружной сети на входе насоса, *P*_{{вх}} = {F(s.InletPressure.Mpa, "0.000")} МПа (по техническим условиям).",
            ]),
            new NoteFormula($"*H*_{{н}} = 100·*P*_{{н}} = **{F(d.PumpHead.Meters, "0.0")} м**.", "6.5", []),
            new NoteParagraph($"Скорость в подводящем трубопроводе — {F(d.SupplyVelocity, "0.00")} м/с ({(d.SupplyVelocity <= d.VelocityLimit ? "не более" : "*больше*")} {F(d.VelocityLimit)} м/с, п. 6.7.1.37)."),
            new NoteParagraph("**Подбор насосов.** Рабочий насос подбирается по каталогу производителя так, чтобы рабочая точка на его характеристике была не ниже требуемой:"),
            new NoteList([
                $"подача *Q* ≥ {F(d.PumpFlow.LitersPerSecond, "0.00")} л/с ({F(m3h, "0.0")} м³/ч);",
                $"напор *H* ≥ {F(d.PumpHead.Meters, "0.0")} м.",
            ]),
            new NoteParagraph("Требования к насосной установке по СП 485:"),
            new NoteList([
                "при любом количестве рабочих агрегатов предусматривается не менее одного резервного, обеспечивающего подачу и напор наиболее производительного рабочего; резервный включается автоматически (п. 6.10.3);",
                $"автоматический водопитатель — жокей-насос с мембранной ёмкостью вместимостью не менее {F(jockey?.Values["minVolume"] ?? 0)} л, вода — от {F(jockey?.Values["waterShareMin"] ?? 0)} до {F(jockey?.Values["waterShareMax"] ?? 0)} % вместимости (п. 6.9.4); при включении пожарного насоса он отключается (п. 6.9.5);",
                "вспомогательный водопитатель — если выход пожарного насоса на режим занимает более 60 с (п. 6.9.8);",
                "насосы относятся ко II категории по обеспеченности подачи воды и к I категории надёжности электроснабжения (п. 6.10.5);",
                "насосная станция — на первом, цокольном или первом подземном этаже, отделена противопожарными стенами (перегородками) 1-го типа и перекрытиями 2-го типа (пп. 6.10.9, 6.10.11);",
                "не менее двух выведенных наружу патрубков DN 80 с соединительными головками для передвижной пожарной техники (п. 6.10.17).",
            ]),
        ]);
    }

    private static NoteSection Water(NoteInputs i, INormsProvider norms)
    {
        var d = i.Design;
        var s = i.DesignSettings;
        var limit = norms.Find("sp485.p6_9_12.minNetworkPressure")?.Values["pressure"] ?? 0;
        var needed = s.InletPressure.Mpa < limit;
        return new NoteSection("7", "Запас воды", [
            new NoteParagraph($"Продолжительность подачи воды для группы помещений {i.RoomGroup} — *t* = {F(d.Duration)} мин (таблица 6.1). Расчётный объём воды на пожаротушение:"),
            new NoteFormula($"*W* = *Q*_{{нас}}·*t*·60 / 1000 = {F(d.PumpFlow.LitersPerSecond, "0.00")}·{F(d.Duration)}·60 / 1000 = **{F(d.WaterVolume, "0.0")} м³**.", "7.1", []),
            new NoteParagraph($"Заполнение пожарного резервуара — не более {F(d.TankMaxFill)} % вместимости (п. 6.9.13), поэтому вместимость резервуара:"),
            new NoteFormula($"*V* = *W* / {F(d.TankMaxFill / 100, "0.00")} = {F(d.WaterVolume, "0.0")} / {F(d.TankMaxFill / 100, "0.00")} = **{F(d.TankVolume, "0.0")} м³**.", "7.2", []),
            new NoteParagraph(needed
                ? $"Давление в наружной сети *P*_{{вх}} = {F(s.InletPressure.Mpa, "0.000")} МПа меньше {F(limit, "0.00")} МПа, поэтому перед насосной установкой **предусматривается пожарный резервуар** (п. 6.9.12)."
                : $"Давление в наружной сети *P*_{{вх}} = {F(s.InletPressure.Mpa, "0.000")} МПа не меньше {F(limit, "0.00")} МПа: пожарный резервуар по п. 6.9.12 не обязателен; приведённый объём — для варианта с резервуаром или при требовании технических условий."),
            new NoteParagraph("В резервуарах предусматриваются устройства, не допускающие расход пожарного запаса воды на другие нужды (п. 6.9.11); учитывается расчётный полезный объём — объём, который способен перекачать насос (п. 6.9.13). Водоснабжение — по 1 категории надёжности (п. 6.9.2)."),
        ]);
    }

    // Требования смежных норм, которые касаются АУПТ (СП 7.13130.2013), — своими словами, со ссылками на пункты.
    private static NoteSection Interfaces() => new("9", "Взаимодействие со смежными системами", [
        new NoteList([
            "по сигналам о срабатывании АУП (сигнализаторы давления узлов управления, сигнализаторы потока жидкости) через систему пожарной сигнализации автоматически отключаются системы общеобменной вентиляции, кондиционирования и воздушного отопления и закрываются нормально открытые противопожарные клапаны (СП 7.13130.2013, п. 6.24);",
            "водяная АУП не освобождает от удаления продуктов горения закрытые автостоянки с парковкой при участии водителей, а также помещения категорий А и Б: исключение п. 7.3 «а» СП 7.13130.2013 для помещений площадью до 200 м² с водяной или пенной АУП на них не распространяется;",
            "состав и адреса сигналов, порядок включения противодымной вентиляции — в задании разделам ПС и ОВ.",
        ]),
    ]);

    // Принцип действия водозаполненной спринклерной АУП: дежурный режим, срабатывание, сигналы, пуск насосов (пп. 6.8.5, 6.9.4, 6.9.5, 6.10.33).
    private static NoteSection Operation(NoteInputs i, INormsProvider norms)
    {
        var d = i.Design;
        var delay = norms.Find("sp485.p6_8_5.delay")?.Values;
        var temperature = i.Temperature is { } t ? $" (номинальная температура срабатывания {t.Nominal} °С, колба — {t.Color})" : "";
        return new NoteSection("10", "Принцип действия установки", [
            new NoteParagraph("Установка — спринклерная водозаполненная: питающие и распределительные трубопроводы постоянно заполнены водой под давлением."),
            new NoteParagraph("*Дежурный режим.* Давление в сети поддерживает автоматический водопитатель — жокей-насос с мембранной ёмкостью (п. 6.9.4); он восполняет утечки и включается по сигнализатору давления. Узел управления закрыт, запорные устройства открыты и контролируются."),
            new NoteParagraph($"*Пожар.* При нагреве разрушается тепловой замок оросителя{temperature}, вода из оросителя орошает очаг. Давление в сети за узлом управления падает, клапан узла открывается, сигнализатор давления выдаёт сигнал о срабатывании"
                + (delay is null ? "" : $" — с задержкой {F(delay["min"]!.Value)}–{F(delay["max"]!.Value)} с для исключения ложных срабатываний (п. 6.8.5)")
                + ". Сигнал поступает в систему пожарной сигнализации и на запуск пожарных насосов."),
            new NoteParagraph("*Пуск насосов.* Пожарный насос включается после автоматической проверки давления в подводящем трубопроводе; при достаточном давлении пуск отменяется до его снижения (п. 6.10.33). При включении пожарного насоса жокей-насос отключается (п. 6.9.5); при отказе рабочего насоса автоматически включается резервный (п. 6.10.3)."),
            new NoteParagraph($"*Тушение.* Насосы подают расход не менее {F(d.PumpFlow.LitersPerSecond, "0.00")} л/с в течение {F(d.Duration, "0")} мин (раздел 7). Установка выключается вручную после ликвидации пожара; вскрывшиеся оросители заменяются из запаса (п. 6.1.15)."),
        ]);
    }

    // Узел управления (п. 6.8).
    private static NoteSection ControlValve(INormsProvider norms)
    {
        var place = norms.Find("sp485.p6_8_2.placement")?.Values;
        var enclosure = norms.Find("sp485.p6_8_3.enclosure")?.Values;
        var platforms = norms.Find("sp485.p6_8_8.platforms")?.Values;
        var items = new List<string>
        {
            "пуск воды на тушение пожара;",
            "заполнение питающих и распределительных трубопроводов водой;",
            "слив воды из питающих и распределительных трубопроводов;",
            "компенсацию утечек из гидравлической системы;",
            "сигнализацию при срабатывании сигнального клапана;",
            "проверку работоспособности и сигнализации о срабатывании;",
            "контроль давления до и после узла управления.",
        };
        var blocks = new List<NoteBlock>
        {
            new NoteParagraph("Узел управления спринклерной водозаполненной установки проектируется по ГОСТ Р 51052 (п. 6.8.1) и устанавливается между подводящим и питающим трубопроводами. Узел обеспечивает (п. 6.8.4):"),
            new NoteList(items),
        };
        if (place is not null)
        {
            blocks.Add(new NoteParagraph($"Узел размещается в насосной станции или в другом помещении с температурой воздуха не ниже {F(place["minAirTemperature"]!.Value)} °С и свободным доступом обслуживающего персонала (п. 6.8.2)"
                + (enclosure is null ? "." : $"; в защищаемом помещении — за противопожарными перегородками 1-го типа с дверями не ниже EI {F(enclosure["doorFireResistance"]!.Value)} или в шкафу с доступом только персонала (п. 6.8.3).")));
        }

        blocks.Add(new NoteParagraph("Перед узлом управления устанавливается запорное устройство (задвижка или дисковый затвор) с контролем положения; за узлом его допускается предусматривать для удобства обслуживания (п. 6.8.7)."
            + (platforms is null ? "" : $" При высоте до маховиков и приводов запорных устройств более {F(platforms["heightAbove"]!.Value)} м от пола предусматриваются площадки или мостики (п. 6.8.8).")
            + " Измерительные приборы должны сниматься для поверки без отключения установки (п. 6.8.10); технические средства, кроме оросителей, приборов и трубопроводов, окрашиваются в красный цвет (п. 6.8.11)."));
        return new NoteSection("11", "Узел управления", blocks);
    }

    // Монтаж и испытания: СП 75.13330 (пп. 6.7.1.25, 6.7.2.7), промывочные устройства (6.7.1.15), заземление (6.7.2.10).
    private static NoteSection Installation(NoteInputs i, INormsProvider norms)
    {
        var flushing = norms.Find("sp485.p6_7_1_15.flushing")?.Values;
        var list = new List<string>
        {
            "монтаж стальных трубопроводов и их крепление — по СП 75.13330 (пп. 6.7.1.25, 6.7.2.7); трубопроводы крепятся к конструкциям здания и не используются как опоры для других конструкций (п. 6.7.1.13);",
            i.Joints is { } joints ? NoteDocx.Escape(joints).TrimEnd('.') + ";" : "соединения стальных трубопроводов — на сварке;",
            "стыки не располагаются на опорах и на изогнутых участках (пп. 6.7.1.28, 6.7.1.31);",
        };
        if (flushing is not null)
        {
            list.Add($"в конце тупиковых и в наиболее удалённых точках кольцевых трубопроводов устанавливаются промывочные заглушки или краны не менее DN {F(flushing["minDn"]!.Value)} (п. 6.7.1.15);");
        }

        list.Add("стальные трубопроводы присоединяются к контуру заземления не менее чем в двух точках (п. 6.7.2.10);");
        list.Add("после монтажа трубопроводы промываются и проходят гидравлические испытания; установка проходит приёмо-сдаточные испытания по ГОСТ Р 50680 с оформлением актов.");
        return new NoteSection("12", "Монтаж и испытания", [
            new NoteParagraph("Монтаж установки выполняется специализированной организацией по рабочей документации и технической документации изготовителей оборудования:"),
            new NoteList(list),
        ]);
    }

    // Эксплуатация: проверка расхода (6.10.36), поверка приборов (6.8.10), запас оросителей (6.1.15).
    private static NoteSection Maintenance(INormsProvider norms) => new("13", "Эксплуатация и техническое обслуживание", [
        new NoteParagraph("Установка обслуживается специализированной организацией по регламенту, составленному на основе технической документации изготовителей оборудования. В регламент входят:"),
        new NoteList([
            "ежедневный контроль давления до и после узла управления, положения запорных устройств, состояния электропитания насосов;",
            "периодическая проверка пуска пожарных насосов и жокей-насоса, работы сигнализаторов давления и потока;",
            "проверка проектного расхода устройством, предусмотренным в насосной станции (п. 6.10.36);",
            "поверка манометров и сигнализаторов без отключения установки (п. 6.8.10);",
            "осмотр оросителей: отсутствие окраски, загрязнения и повреждений тепловых замков; замена вскрывшихся оросителей из запаса (п. 6.1.15).",
        ]),
        new NoteParagraph("Отключение установки на время ремонта допускается только с уведомлением и принятием компенсирующих мер."),
    ]);

    private static NoteSection Conclusion(NoteInputs i)
    {
        var d = i.Design;
        return new NoteSection("", "Заключение", [
            new NoteParagraph($"Для объекта «{i.ObjectName}» запроектирована спринклерная водозаполненная установка водяного пожаротушения по СП 485.1311500.2020:"),
            new NoteList([
                $"оросителей — **{i.Layout.Sprinklers.Count} шт.** (*K* = {F(i.DesignSettings.SprinklerK.Value)}), запас — по п. 6.1.15;",
                $"расчётный расход — {F(d.DesignFlow.LitersPerSecond, "0.00")} л/с, расход насоса — **{F(d.PumpFlow.LitersPerSecond, "0.00")} л/с** ({F(d.PumpFlow.LitersPerSecond * 3.6, "0.0")} м³/ч);",
                $"требуемый напор насоса — **{F(d.PumpHead.Meters, "0.0")} м** ({F(d.PumpPressure.Mpa, "0.000")} МПа);",
                $"объём воды на пожаротушение — {F(d.WaterVolume, "0.0")} м³, вместимость резервуара — {F(d.TankVolume, "0.0")} м³.",
            ]),
            new NoteParagraph("Прочтения норм, принятые в расчёте (диктующая зона, местные сопротивления, расход диктующего оросителя), указаны в разделе 5 и подлежат согласованию с руководителем. Результаты, полученные программой Water_Auto, подлежат проверке специалистом по противопожарным системам перед выпуском."),
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
        yield return new NoteList(issues.Select(x => x.Clause is null ? x.Message : $"{x.Message} ({ClauseRef(x.Clause)})").ToList());
    }

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
