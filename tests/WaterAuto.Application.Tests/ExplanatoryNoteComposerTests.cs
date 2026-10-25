using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;
using WaterAuto.Reports;

namespace WaterAuto.Application.Tests;

public class ExplanatoryNoteComposerTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static NoteDocument Compose()
    {
        var rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), "Узел управления")]);
        var layout = SprinklerLayouter.Layout(plan, rules);
        var routing = PipeRouter.Route(plan, layout, Norms, rules, new Pressure(0.3));
        var takeoff = MaterialTakeoffBuilder.Build(routing.Routes, layout.Sprinklers.Count, Norms, "ГОСТ 3262", new SprinklerSpec("Ороситель спринклерный водяной", "по каталогу"));
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
        _ => "",
    }));

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
            ("", "Заключение"),
        ]);
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
    public void Undecided_calculations_are_pending_and_name_the_questions()
    {
        var note = Compose();
        foreach (var number in new[] { "5", "6", "7" })
        {
            note.Sections.Single(s => s.Number == number).Blocks.ShouldContain(b => b is NotePending);
        }

        AllText(note.Sections.Single(s => s.Number == "5")).ShouldContain("В-01");
        AllText(note.Sections.Single(s => s.Number == "6")).ShouldContain("В-03");
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
