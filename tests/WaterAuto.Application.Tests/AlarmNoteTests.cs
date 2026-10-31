using WaterAuto.Alarm;
using WaterAuto.Application;
using WaterAuto.Core;
using WaterAuto.Norms;
using WaterAuto.Reports;

namespace WaterAuto.Application.Tests;

// Пояснительная записка АПС: объясняет ход расчёта и показывает числа из раскладки, проверок и АКБ, ничего не считая заново.
public class AlarmNoteTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);
    private static readonly NoteTitle Title = new("Орг", "Тема", "Документ", "Автор", "Рук", "Город 2026", "Объект");

    private static string Text(NoteDocument doc) => string.Join("\n", doc.Sections.SelectMany(s => s.Blocks).Select(b => b switch
    {
        NoteParagraph p => p.Text,
        NoteFormula f => f.Expression + "\n" + string.Join("\n", f.Legend),
        NoteList l => string.Join("\n", l.Items),
        NoteTable t => string.Join("\n", t.Rows.Select(r => string.Join(" | ", r))),
        NotePending p => "PENDING " + p.Text,
        _ => "",
    }));

    [Fact]
    public void Explains_choice_placement_zones_and_shows_layout_numbers()
    {
        var zone = new DetectorZone("R1", "Автостоянка", new Polygon([new(0, 0), new(20000, 0), new(20000, 10000), new(0, 10000)]), new Length(3), DetectorKind.Smoke, DetectionAlgorithm.C, true);
        var layout = DetectorLayouter.Layout([zone], Norms);
        var issues = layout.Issues.Concat(ZoneChecks.Check([zone], layout, Norms)).ToList();
        var zoning = AlarmZoning.Build([zone], layout, Norms, manualIsolator: false);

        var doc = AlarmNote.Build(Title, [zone], layout, issues, [], null, Norms, zoning);
        var text = Text(doc);
        text.ShouldContain($"Ручные извещатели — {zoning.Manuals.Count} шт.");
        text.ShouldContain("ЗКСПС-1 | Автостоянка");

        doc.Sections.Select(s => s.Heading).ShouldContain("Размещение извещателей");
        text.ShouldContain("6.6.16");
        text.ShouldContain("двумя ИП");
        text.ShouldContain($"Всего извещателей — {layout.Detectors.Count} шт.");
        text.ShouldContain("Автостоянка | 200 | 6,4");
        text.ShouldContain("2000 м²");
        text.ShouldContain("PENDING Расчёт электропитания не выполнен");
    }

    [Fact]
    public void Power_section_puts_budget_numbers_into_formula_B11()
    {
        var battery = new BatteryModel("АБ 1217С", new Charge(17), 80, new Dictionary<TimeSpan, Current>(), "РЭ АБ");
        var budget = new PowerBudget(new Voltage(12),
            [new PowerLine("С2000-КДЛ", 1, new Current(0.085), new Current(0.4), "", "РЭ КДЛ"), new PowerLine("С2000-АР8", 0, null, null, "", "РЭ АР8")],
            new Current(0.085), new Current(0.4), new Current(0.04), battery, 0.974, 1.633, new Charge(4.553), TimeSpan.FromHours(34), 1.25);
        ControlPoint[] points = [new("cu.1", "Узел управления DN100", SignalKind.PressureSwitch, "СП 485, п. 6.8.7", "cu", "УУ1", 1)];

        var text = Text(AlarmNote.Build(Title, [], new DetectorLayout([], new Dictionary<string, int>(), []), [], points, budget, Norms));

        text.ShouldContain("1,25·[0,974·(0,085 + 0,04)·24 + 1,633·(0,4 + 0,04)·1] = 4,55 А·ч");
        text.ShouldContain("требуемая ёмкость обеспечена");
        text.ShouldContain("= 34 ч");
        text.ShouldContain("УУ1 | Узел управления DN100");
        text.ShouldNotContain("С2000-АР8"); // нулевые строки не показываются
    }
}
