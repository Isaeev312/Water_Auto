using System.Globalization;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application;

/// <summary>
/// Техническое задание на взаимодействие АУПТ с системой пожарной сигнализации (ADR 0012, этап 1): сигналы — из перечня
/// точек управления, приборы и питание — из расчёта АКБ. Ручного ввода оборудования АУПТ нет; что программа не знает
/// (помещения приборов, линии связи с пультом), выделено как незаполненное.
/// </summary>
public static class InteractionSpec
{
    public static NoteDocument Build(NoteTitle title, IReadOnlyList<ControlPoint> points, PowerBudget? budget)
    {
        ArgumentNullException.ThrowIfNull(points);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var sections = new List<NoteSection>
        {
            new("1", "Общие положения",
            [
                new NoteParagraph("Задание выдаётся разработчику раздела «Система пожарной сигнализации» для приёма сигналов автоматической установки пожаротушения (АУПТ) и их передачи на пульт."),
                new NoteParagraph("Обозначения устройств, номера позиций и секций — по перечню сигналов АУПТ в АПС (aps_signals.xlsx) и принципиальной схеме насосной станции."),
            ]),
            new("2", "Сигналы, передаваемые в систему пожарной сигнализации",
            [
                new NoteParagraph("Предусмотреть приём и контроль следующих сигналов (по одному входу на сигнал):"),
                new NoteTable("Перечень сигналов АУПТ",
                    ["№", "Обозначение", "Секция", "Оборудование", "Сигнал", "Основание"],
                    [.. points.Select((p, i) => (IReadOnlyList<string>)[
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        p.Designation,
                        p.Section?.ToString(CultureInfo.InvariantCulture) ?? "насосная",
                        p.Equipment,
                        SignalListXlsx.KindText(p.Kind),
                        p.Clause ?? "не указано"])]),
                new NoteParagraph("Цепи датчиков положения и сигнализаторов давления контролировать на обрыв и короткое замыкание."),
            ]),
        };

        if (budget is null)
        {
            sections.Add(new("3", "Приборы и электропитание", [new NotePending("Набор приборов и расчёт ёмкости АКБ не выполнены: нет каталога приборов питания (data/catalog/alarm_power.json).")]));
        }
        else
        {
            var b = budget.Battery;
            sections.Add(new("3", "Приборы и электропитание",
            [
                new NoteParagraph("Предусмотреть адресное пространство и программирование приборов:"),
                new NoteList([.. budget.Lines.Where(l => l.Standby is null || l.Count > 0).Select(l => $"{l.Name} — {l.Count} шт.")]),
                new NoteParagraph(string.Create(ru, $"Питание приборов — {budget.Voltage.Volts:0.#} В от резервированного источника с АКБ. Ток потребления в дежурном режиме — {budget.Standby.Amperes * 1000:0.#} мА, в режиме «Пожар» — {budget.Alarm.Amperes * 1000:0.#} мА, собственный ток источника от АКБ — {budget.SupplyOwn.Amperes * 1000:0.#} мА.")),
                new NoteParagraph(budget.Required is { } r
                    ? string.Create(ru, $"Требуемая ёмкость АКБ по прил. Б СП 6.13130.2025 — {r.AmpereHours:0.##} А·ч; принята {b.Name} ({b.Nominal.AmpereHours:0.##} А·ч). Расчёт — aps_battery.xlsx.")
                    : "Ёмкость АКБ не определена — см. замечания расчёта (aps_battery.xlsx)."),
                new NotePending("Помещение установки приборов, линия связи с пультом (RS-485) и питание от сети — указать проектировщику АПС."),
            ]));
        }

        return new NoteDocument(title with { Title = "Техническое задание на взаимодействие АУПТ с системой пожарной сигнализации" }, sections, []);
    }
}
