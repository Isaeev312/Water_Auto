using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;
using WaterAuto.Reports;

namespace WaterAuto.Application.Tests;

public class ExplanatoryNoteComposerTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static NoteDocument Compose(bool hydrants = false)
    {
        var rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), "Узел управления")]);
        var layout = SprinklerLayouter.Layout(plan, rules);
        var routing = PipeRouter.Route(plan, layout, Norms, rules, new Pressure(0.3));
        var settings = new DesignSettings("ГОСТ 3262", new KFactor(0.47), new Length(4), new Length(25), 100, new Pressure(0.1), DesignVelocity: 5);
        var hs = new HydrantSettings("II", "В", "С0", 40, new Length(12), new Length(3.3), 50, 16, 20, new Length(1.35), new Length(15), 65);
        var design = DesignCalculation.Run(routing.Routes[0], Norms, rules, settings, hydrants: hydrants ? new HydrantInput(hs, HydrantDemand.Resolve(Norms, hs).Demand!) : null);
        var takeoff = MaterialTakeoffBuilder.Build([design.Route], layout.Sprinklers.Count, Norms, "ГОСТ 3262", new SprinklerSpec("Ороситель спринклерный водяной", "по каталогу"));
        using var registry = new StreamReader(RepoPaths.Registry);
        var inputs = new NoteInputs(
            new NoteTitle("[Наименование вуза]", "Автоматическая установка водяного пожаротушения", "Пояснительная записка", "[ФИО студента]", "[ФИО руководителя]", "[Город] 2027"),
            "Подземная автостоянка (демо)",
            "2",
            ConstructionClass.K0,
            plan,
            rules,
            layout,
            routing,
            takeoff,
            design,
            settings,
            LiteratureRegistry.Parse(registry),
            ["SP485-izm"]);
        return ExplanatoryNoteComposer.Compose(inputs, Norms);
    }

    private static string AllText(NoteSection s) => string.Join("\n", s.Blocks.Select(b => b switch
    {
        NoteParagraph p => p.Text,
        NotePending p => p.Text,
        NoteList l => string.Join("\n", l.Items),
        NoteTable t => t.Caption + "\n" + string.Join("\n", t.Rows.Select(r => string.Join(" | ", r))),
        NoteFormula f => f.Expression + "\n" + string.Join("\n", f.Legend),
        _ => "",
    }));

    [Fact]
    public void Hydrant_section_cites_Zh2_Zh3_and_B29()
    {
        var text = AllText(Compose(hydrants: true).Sections.Single(s => s.Number == "5"));
        text.ShouldContain("СН КР 40-04:2025");
        text.ShouldContain("Ж.2");
        text.ShouldContain("Ж.3");
        text.ShouldContain("(Б.29)");
        text.ShouldContain("Б.1.3.16");
        AllText(Compose().Sections.Single(s => s.Number == "5")).ShouldNotContain("Ж.2");
    }

    [Fact]
    public void Sections_follow_the_planned_structure()
    {
        var note = Compose();
        note.Sections.Select(s => (s.Number, s.Heading)).ShouldBe([
            ("", "Введение"),
            ("1", "Исходные данные"),
            ("2", "Параметры установки"),
            ("3", "Размещение оросителей"),
            ("4", "Трассировка трубопроводов"),
            ("5", "Гидравлический расчёт"),
            ("6", "Насосная установка"),
            ("7", "Запас воды"),
            ("8", "Спецификация"),
            ("9", "Взаимодействие со смежными системами"),
            ("10", "Принцип действия установки"),
            ("11", "Узел управления"),
            ("12", "Монтаж и испытания"),
            ("13", "Эксплуатация и техническое обслуживание"),
            ("", "Заключение"),
        ]);
    }

    [Fact]
    public void Interfaces_cite_SP_7()
    {
        var text = AllText(Compose().Sections.Single(s => s.Number == "9"));
        text.ShouldContain("СП 7.13130.2013, п. 6.24");
        text.ShouldContain("п. 7.3 «а»");
    }

    [Fact]
    public void Parameters_come_from_the_norms_data_with_clauses()
    {
        var text = AllText(Compose().Sections.Single(s => s.Number == "2"));
        text.ShouldContain("0,12 л/(с·м²) | табл. 6.1");
        text.ShouldContain("120 м²");
        text.ShouldContain("60 мин");
        text.ShouldContain("3,5 м");
        text.ShouldContain("1,5 м | п. 6.2.21");
        text.ShouldContain("1,75 м | п. 6.2.21");
    }

    [Fact]
    public void Layout_routing_and_specification_report_actual_results()
    {
        var note = Compose();
        AllText(note.Sections.Single(s => s.Number == "3")).ShouldContain("54");
        AllText(note.Sections.Single(s => s.Number == "4")).ShouldContain("DN");
        var spec = AllText(note.Sections.Single(s => s.Number == "8"));
        spec.ShouldContain("54");
        spec.ShouldContain("запас");
    }

    [Fact]
    public void Calculation_sections_are_complete_with_formulas_and_clauses()
    {
        var note = Compose();
        foreach (var number in new[] { "5", "6", "7" })
        {
            var section = note.Sections.Single(s => s.Number == number);
            section.Blocks.ShouldNotContain(b => b is NotePending, $"раздел {number} не должен содержать заготовок");
            section.Blocks.ShouldContain(b => b is NoteFormula, $"в разделе {number} нет формул");
        }

        var hydraulics = AllText(note.Sections.Single(s => s.Number == "5"));
        foreach (var clause in new[] { "Б.1", "Б.2", "Б.3", "Б.5", "Б.6", "Б.23", "Б.1.3.13", "6.7.1.37" })
        {
            hydraulics.ShouldContain(clause);
        }

        hydraulics.ShouldContain("2,16");
        hydraulics.ShouldContain("*Принято:*");
        var pump = AllText(note.Sections.Single(s => s.Number == "6"));
        foreach (var clause in new[] { "Б.30", "6.10.3", "6.9.4" })
        {
            pump.ShouldContain(clause);
        }

        var water = AllText(note.Sections.Single(s => s.Number == "7"));
        water.ShouldContain("6.9.13");
        water.ShouldContain("6.9.12");
        note.Sections.Single(s => s.Heading == "Заключение").Blocks.ShouldNotContain(b => b is NotePending);
    }

    [Fact]
    public void Calculation_numbers_in_the_note_match_the_design_result()
    {
        var note = Compose();
        var table = note.Sections.Single(s => s.Number == "5").Blocks.OfType<NoteTable>().Single(t => t.Caption.Contains("Гидравлический расчёт", StringComparison.Ordinal));
        table.Rows.Count.ShouldBeGreaterThan(3);
        AllText(note.Sections.Single(s => s.Number == "6")).ShouldContain("м³/ч");
    }

    [Fact]
    public void References_are_formatted_from_the_registry()
    {
        var reference = Compose().References.ShouldHaveSingleItem();
        reference.ShouldStartWith("СП 485.1311500.2020. Установки пожаротушения автоматические. Нормы и правила проектирования : свод правил");
    }

    [Fact]
    public void Composed_note_is_written_to_docx()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.docx");
        NoteDocx.Write(path, Compose());
        new FileInfo(path).Length.ShouldBeGreaterThan(0);
        File.Delete(path);
    }
}
