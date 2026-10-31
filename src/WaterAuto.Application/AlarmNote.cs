using System.Globalization;
using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application;

/// <summary>
/// Пояснительная записка по АПС (ADR 0012): ход расчёта словами и с формулами, чтобы проектировщик без опыта в АПС видел,
/// откуда каждое число и какой пункт его требует. Собирается из раскладки ИП, проверок ЗКСПС, перечня сигналов АУПТ и расчёта
/// АКБ; ничего не считает заново.
/// </summary>
public static class AlarmNote
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static NoteDocument Build(NoteTitle title, IReadOnlyList<DetectorZone> zones, DetectorLayout layout, IReadOnlyList<Issue> issues, IReadOnlyList<ControlPoint> points, PowerBudget? budget, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(norms);
        var sections = new List<NoteSection>
        {
            new("1", "Исходные данные и границы расчёта",
            [
                new NoteParagraph("Раздел выполнен для автоматической пожарной сигнализации (АПС), связанной с водяной спринклерной установкой пожаротушения (АУПТ). Нормы: СП 484.1311500.2020 с Изменением № 1 — размещение извещателей и состав системы; СП 6.13130.2025 — электропитание и ёмкость аккумуляторных батарей; ГОСТ Р 53325-2012 — сигналы приборов управления."),
                new NoteParagraph("Какие помещения защищаются АПС, тип извещателей, алгоритм принятия решения о пожаре и адресность приняты проектировщиком и заданы в исходных данных (раздел alarm проекта). Программа не решает, нужна ли сигнализация в помещении, — она расставляет извещатели по заданию и проверяет результат по нормам."),
                new NoteTable("Защищаемые помещения", ["Помещение", "Высота до перекрытия, м", "Тип ИП", "Алгоритм", "Адресность", "Балки"],
                    [.. zones.Select(z => (IReadOnlyList<string>)[z.RoomName, z.CeilingHeight.Meters.ToString("0.##", Ru), Kind(z.Kind), z.Algorithm.ToString(), z.Addressable ? "адресные" : "безадресные", z.Beams.Count == 0 ? "нет" : string.Create(Ru, $"{z.Beams.Count} шт., до {z.Beams.Max(b => b.Height.Meters):0.##} м")])]),
            ]),
        };

        sections.Add(new("2", "Выбор извещателей и алгоритма", Choice(zones, norms)));
        sections.Add(new("3", "Размещение извещателей", Placement(zones, layout, issues, norms)));
        sections.Add(new("4", "Зоны контроля и прибор", Zones(zones, layout, issues)));
        sections.Add(new("5", "Сигналы от установки пожаротушения", Signals(points)));
        sections.Add(new("6", "Электропитание и аккумуляторная батарея", Power(budget)));
        return new NoteDocument(title with { Title = "Пожарная сигнализация. Пояснительная записка к расчёту" }, sections, []);
    }

    private static List<NoteBlock> Choice(IReadOnlyList<DetectorZone> zones, INormsProvider norms)
    {
        var blocks = new List<NoteBlock>
        {
            new NoteParagraph("Точечные дымовые ИП применяются в помещениях высотой до 12 м, радиус зоны контроля не более 6,4 м (п. 6.6.16); точечные тепловые — до 9 м, радиус не более 3,5 м (п. 6.6.15). Зона контроля точечного ИП — круг (п. 6.6.5): помещение защищено, когда каждая его точка попадает в круг хотя бы одного ИП."),
            new NoteParagraph("Алгоритм принятия решения о пожаре (п. 6.4): A — по одному срабатыванию, B — по повторному срабатыванию или второму ИП за 60 с, C — по двум разным ИП. Для зон, которые формируют сигналы управления АУП, алгоритмы A и B не допускаются (п. 6.4.5). От алгоритма и адресности зависит, сколькими ИП должна контролироваться каждая точка помещения: при A и B с адресными ИП — одним (п. 6.6.1), с безадресными — двумя; при C — всегда двумя (п. 6.6.2). Двукратный контроль примерно удваивает число извещателей."),
        };
        foreach (var z in zones)
        {
            var (rules, _) = DetectorRules.Resolve(norms, z.Kind, z.Algorithm, z.Addressable);
            if (rules is null)
            {
                continue;
            }

            blocks.Add(new NoteParagraph(string.Create(Ru, $"«{z.RoomName}»: {Kind(z.Kind)} ИП, радиус зоны контроля *R* = {rules.Radius.Meters:0.#} м, алгоритм {z.Algorithm}, {(z.Addressable ? "адресные" : "безадресные")} — каждая точка контролируется {(rules.Coverage >= 2 ? "двумя" : "одним")} ИП; высота помещения {z.CeilingHeight.Meters:0.##} м не больше предела {rules.MaxRoomHeight.Meters:0.#} м.")));
        }

        return blocks;
    }

    private static List<NoteBlock> Placement(IReadOnlyList<DetectorZone> zones, DetectorLayout layout, IReadOnlyList<Issue> issues, INormsProvider norms)
    {
        var blocks = new List<NoteBlock>
        {
            new NoteParagraph("Извещатели расставлены равномерной сеткой. Шаг сетки выбирается так, чтобы круги зон контроля перекрывали помещение без пропусков: при однократном контроле ячейка сетки — квадрат, вписанный в круг радиуса *R*, при двукратном — квадрат со стороной *R*. После расстановки покрытие проверяется по сетке точек через 0,25 м по всему помещению; если какая-то точка видна меньшему числу ИП, чем требуется, сетка уплотняется."),
            new NoteFormula("*a* = *R*·√2 — при контроле каждой точки одним ИП; *a* = *R* — при контроле двумя ИП,", "3.1", ["где *a* — шаг сетки извещателей, м; *R* — радиус зоны контроля, м."]),
            new NoteParagraph("Отступ от стен и выступающих более чем на 0,25 м конструкций — не менее 0,5 м (п. 6.6.36); расстояние между ИП при алгоритме C — не менее 1,2 м (п. 6.5.1). При балках выше 10 % высоты помещения извещатели ставятся в полях между балками, а расстояние поперёк балок между ИП и от ИП до грани балки ограничено таблицей 6.2."),
        };
        var rows = new List<IReadOnlyList<string>>();
        foreach (var z in zones)
        {
            var (rules, _) = DetectorRules.Resolve(norms, z.Kind, z.Algorithm, z.Addressable);
            var count = layout.CountByRoom.GetValueOrDefault(z.RoomId);
            var area = z.Outline.AreaMm2 / 1e6;
            var step = rules is null ? "—" : string.Create(Ru, $"{(rules.Coverage >= 2 ? rules.Radius.Meters : rules.Radius.Meters * Math.Sqrt(2)):0.##}");
            var beams = z.Beams.Count > 0 && rules is not null && DetectorRules.BeamSpacing(norms, z.Kind, z.CeilingHeight, new Length(z.Beams.Max(b => b.Height.Meters))) is { } row
                ? string.Create(Ru, $"≤ {row.Across.Meters:0.##} / ≤ {row.ToWall.Meters:0.##}")
                : "—";
            rows.Add([z.RoomName, string.Create(Ru, $"{area:0}"), step, beams, count.ToString(Ru), count > 0 ? string.Create(Ru, $"{area / count:0.#}") : "—"]);
        }

        blocks.Add(new NoteTable("Результат расстановки", ["Помещение", "Площадь, м²", "Шаг сетки *a*, м", "Поперёк балок / до грани, м (табл. 6.2)", "ИП, шт.", "м² на один ИП"], rows));
        blocks.Add(new NoteParagraph(string.Create(Ru, $"Всего извещателей — {layout.Detectors.Count} шт. Расстановка — на плане АПС (aps_plan.dxf), обозначения ИП — «помещение.D номер».")));
        foreach (var i in issues.Where(i => i.Code.StartsWith("aps.layout", StringComparison.Ordinal) && i.Level != IssueLevel.Info))
        {
            blocks.Add(new NotePending($"{i.Level}: {i.Message}"));
        }

        return blocks;
    }

    private static List<NoteBlock> Zones(IReadOnlyList<DetectorZone> zones, DetectorLayout layout, IReadOnlyList<Issue> issues)
    {
        var area = zones.Sum(z => z.Outline.AreaMm2) / 1e6;
        var blocks = new List<NoteBlock>
        {
            new NoteParagraph("Зона контроля пожарной сигнализации (ЗКСПС) — не больше 2000 м² и не больше пяти помещений одного этажа и пожарного отсека (п. 6.3.4). Единичная неисправность линии не должна отключать больше 24 автоматических ИП и одного ручного (п. 6.3.5) — для адресной линии это обеспечивается изоляторами короткого замыкания, которые ставятся в линию между группами извещателей. На один прибор — не больше 512 ИП и 12 000 м² (п. 6.1.5); запас ёмкости прибора — не меньше 20 % при известной планировке (п. 5.20)."),
            new NoteParagraph(string.Create(Ru, $"Защищаемая площадь — {area:0} м², извещателей — {layout.Detectors.Count} шт.{(layout.Detectors.Count > 0 ? string.Create(Ru, $", запас по ёмкости прибора на 512 ИП — {(512 - layout.Detectors.Count) * 100.0 / 512:0} %") : "")}.")),
        };
        var zoneIssues = issues.Where(i => i.Code.StartsWith("aps.zone", StringComparison.Ordinal) || i.Code.StartsWith("aps.panel", StringComparison.Ordinal)).ToList();
        blocks.Add(zoneIssues.Count == 0
            ? new NoteParagraph("Ограничения по ЗКСПС и ёмкости прибора выполнены.")
            : new NoteList([.. zoneIssues.Select(i => i.Message)]));
        blocks.Add(new NotePending("Деление на ЗКСПС, расстановка ручных извещателей (п. 6.6.27) и изоляторов КЗ — решение проектировщика АПС; в расчёте не выполнены."));
        return blocks;
    }

    private static List<NoteBlock> Signals(IReadOnlyList<ControlPoint> points)
    {
        if (points.Count == 0)
        {
            return [new NoteParagraph("Сигналы от установки пожаротушения в проекте не сформированы (нет насосной станции).")];
        }

        return
        [
            new NoteParagraph("Спринклерная установка без принудительного пуска включается сама, поэтому сигналы управления ей от сигнализации не нужны (СП 484, п. 7.3.1). В систему передаются сигналы от установки: срабатывание сигнализаторов давления узлов управления, положение задвижек и затворов с датчиками положения (СП 485, п. 6.8.7), обобщённые сигналы шкафа насосов «Пуск», «Неисправность» и «Автоматика отключена» (ГОСТ Р 53325, п. 7.4.5). Каждый сигнал занимает один вход адресного модуля."),
            new NoteTable("Сигналы АУПТ в АПС", ["Обозначение", "Оборудование", "Сигнал", "Основание"],
                [.. points.Select(p => (IReadOnlyList<string>)[p.Designation, p.Equipment, p.Kind.Text(), p.Clause ?? "состав оборудования"])]),
            new NoteParagraph(string.Create(Ru, $"Всего сигналов — {points.Count}; перечень — aps_signals.xlsx, распределение по модулям и входам — aps_addresses.xlsx, требования смежному разделу — aps_interaction.docx.")),
        ];
    }

    private static List<NoteBlock> Power(PowerBudget? b)
    {
        if (b is null)
        {
            return [new NotePending("Расчёт электропитания не выполнен: нет каталога приборов питания или сигналов АУПТ.")];
        }

        var kd = b.StandbyLoadFactor is { } d ? d.ToString("0.###", Ru) : "—";
        var kp = b.AlarmLoadFactor is { } a ? a.ToString("0.###", Ru) : "—";
        var kAging = b.AgingFactor is { } g ? g.ToString("0.##", Ru) : "—";
        return
        [
            new NoteParagraph("Приборы сигнализации относятся к I категории надёжности электроснабжения (СП 6.13130.2025, п. 5.21): основное питание от сети, резервное — от аккумуляторной батареи резервированного источника. Батарея должна обеспечить работу 24 ч в дежурном режиме и затем 1 ч в режиме «Пожар» (прил. Б СП 6)."),
            new NoteTable("Токи потребления", ["Прибор", "Кол.", "Дежурный, мА", "«Пожар», мА", "Источник данных"],
                [.. b.Lines.Where(l => l.Count > 0).Select(l => (IReadOnlyList<string>)[l.Name, l.Count.ToString(Ru), Milli(l.Standby), Milli(l.Alarm), l.Source])]),
            new NoteParagraph(string.Create(Ru, $"Суммарный ток в дежурном режиме *I*_{{д}} = {b.Standby.Amperes * 1000:0.#} мА, в режиме «Пожар» *I*_{{п}} = {b.Alarm.Amperes * 1000:0.#} мА, собственный ток источника от батареи *I*_{{ибэ}} = {b.SupplyOwn.Amperes * 1000:0.#} мА. Адресные извещатели и модули питаются от линии контроллера — их ток входит в ток контроллера.")),
            new NoteFormula(string.Create(Ru, $"*C* = *K*_{{стр}}·[*K*_{{н.д}}·(*I*_{{д}} + *I*_{{ибэ}})·*t*_{{д}} + *K*_{{н.п}}·(*I*_{{п}} + *I*_{{ибэ}})·*t*_{{п}}] = {kAging}·[{kd}·({b.Standby.Amperes:0.###} + {b.SupplyOwn.Amperes:0.###})·24 + {kp}·({b.Alarm.Amperes:0.###} + {b.SupplyOwn.Amperes:0.###})·1] = {(b.Required is { } r ? r.AmpereHours.ToString("0.##", Ru) : "—")} А·ч,"), "6.1",
            [
                "где *K*_{стр} = 100 % / *S* — коэффициент старения (формула Б.2), *S* — ёмкость батареи в конце срока службы по паспорту, %;",
                "*K*_{н.д}, *K*_{н.п} — коэффициенты нагрузки по паспорту батареи для 24-часового и 1-часового разряда (формула Б.1.1; во втором слагаемом принят ток режима «Пожар»);",
                "*t*_{д} = 24 ч, *t*_{п} = 1 ч — время работы от батареи (п. Б.2).",
            ]),
            new NoteParagraph(string.Create(Ru, $"Принята батарея {b.Battery.Name} ёмкостью {b.Battery.Nominal.AmpereHours:0.##} А·ч{(b.Required is { } rq ? (rq.AmpereHours <= b.Battery.Nominal.AmpereHours ? " — требуемая ёмкость обеспечена" : " — **требуемая ёмкость не обеспечена**, нужна батарея большей ёмкости") : "")}. Время работы в режиме «Пожар» по формуле Б.3: *t* = *C*_{{АКБ}} / (*I*_{{п}}·*K*_{{стр}}) = {(b.Backup is { } t ? t.TotalHours.ToString("0.#", Ru) : "—")} ч. Подробно — aps_battery.xlsx.")),
        ];
    }

    private static string Milli(Current? c) => c is { } v ? (v.Amperes * 1000).ToString("0.#", Ru) : "от линии";

    private static string Kind(DetectorKind kind) => kind == DetectorKind.Smoke ? "дымовые" : "тепловые";
}
