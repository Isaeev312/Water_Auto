using WaterAuto.Application;
using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Application.Tests;

// ТЗ на взаимодействие АУПТ с АПС (ADR 0012, этап 1): собирается из перечня сигналов и расчёта АКБ, ручного ввода нет.
public class InteractionSpecTests
{
    private static readonly NoteTitle Title = new("Орг", "Тема", "Документ", "Автор", "Рук", "Город 2026", "Объект");

    private static readonly ControlPoint[] Points =
    [
        new("cabinet.pumpStart.1", "Шкаф управления пожарными насосами", SignalKind.PumpStart, "ГОСТ Р 53325-2012, п. 7.4.5 (сверить)", "cabinet", "ШУН", null),
        new("cu.1", "Узел управления DN100", SignalKind.PressureSwitch, null, "cu", "УУ1", 1),
        new("cuValve.1", "Затвор DN100", SignalKind.ValvePosition, "СП 485.1311500.2020, п. 6.8.7", "cuValve", "ЗД1", 1),
    ];

    [Fact]
    public void Lists_every_signal_with_designation_and_basis()
    {
        var doc = InteractionSpec.Build(Title, Points, null);

        var table = doc.Sections.SelectMany(s => s.Blocks).OfType<NoteTable>().First(t => t.Caption.Contains("сигнал", StringComparison.OrdinalIgnoreCase));
        table.Rows.Count.ShouldBe(3);
        table.Rows[1].ShouldContain("УУ1");
        table.Rows[1].ShouldContain("1");
        table.Rows[1].ShouldContain("не указано");
        table.Rows[0].ShouldContain("Пуск (обобщённый выход)");
    }

    [Fact]
    public void Power_section_comes_from_the_battery_budget()
    {
        var battery = new BatteryModel("АБ 1217С", new Charge(17), 80, new Dictionary<TimeSpan, Current>(), "РЭ АБ");
        var budget = new PowerBudget(new Voltage(12),
            [new PowerLine("С2000-КДЛ", 1, new Current(0.085), new Current(0.4), "", "РЭ КДЛ"), new PowerLine("С2000-АР8", 1, null, null, "", "РЭ АР8")],
            new Current(0.085), new Current(0.4), new Current(0.04), battery, 0.97, 1.63, new Charge(4.553), TimeSpan.FromHours(34));

        var doc = InteractionSpec.Build(Title, Points, budget);

        var text = string.Join("\n", doc.Sections.SelectMany(s => s.Blocks).Select(b => b switch
        {
            NoteParagraph p => p.Text,
            NoteList l => string.Join("\n", l.Items),
            NoteTable t => string.Join("\n", t.Rows.Select(r => string.Join(" | ", r))),
            _ => "",
        }));
        text.ShouldContain("С2000-КДЛ");
        text.ShouldContain("С2000-АР8 — 1 шт.");
        text.ShouldContain("4,55");
        text.ShouldContain("12 В");
    }

    [Fact]
    public void Without_budget_the_power_part_is_marked_pending()
    {
        var doc = InteractionSpec.Build(Title, Points, null);

        doc.Sections.SelectMany(s => s.Blocks).OfType<NotePending>().ShouldNotBeEmpty();
    }
}
