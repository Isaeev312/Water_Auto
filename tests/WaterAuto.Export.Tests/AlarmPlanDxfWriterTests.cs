using ACadSharp.Entities;
using WaterAuto.Core;

namespace WaterAuto.Export.Tests;

public class AlarmPlanDxfWriterTests
{
    [Fact]
    public void Detectors_go_to_their_layer_as_blocks_with_ids_and_rooms_get_a_summary()
    {
        var outline = new Polygon([new(0, 0), new(10000, 0), new(10000, 5000), new(0, 5000)]);
        var zone = new DetectorZone("R1", "Стоянка", outline, new Length(3), DetectorKind.Smoke, DetectionAlgorithm.C, true);
        var layout = new DetectorLayout(
            [new PlacedDetector("R1.D1", "R1", new PlanPoint(2500, 2500), DetectorKind.Smoke), new PlacedDetector("R1.D2", "R1", new PlanPoint(7500, 2500), DetectorKind.Heat)],
            new Dictionary<string, int> { ["R1"] = 2 },
            []);
        Issue[] issues = [new Issue("aps.layout.wallOffset", IssueLevel.Info, "узко", "R1", Location: new PlanPoint(5000, 2500)), new Issue("other", IssueLevel.Info, "не на план", Location: new PlanPoint(1, 1))];

        var doc = AlarmPlanDxfWriter.Build(layout, [zone], issues);

        var inserts = doc.Entities.OfType<Insert>().Where(i => i.Layer.Name == AlarmPlanDxfWriter.DetectorLayer).ToList();
        inserts.Count.ShouldBe(2);
        inserts.Select(i => i.Block.Name).ShouldBe([AlarmPlanDxfWriter.SmokeBlock, AlarmPlanDxfWriter.HeatBlock], ignoreOrder: true);
        inserts.Select(i => i.Attributes.Single(a => a.Tag == "ID").Value).ShouldBe(["R1.D1", "R1.D2"], ignoreOrder: true);
        doc.Entities.OfType<LwPolyline>().Count(p => p.Layer.Name == AlarmPlanDxfWriter.ContourLayer).ShouldBe(1);
        doc.Entities.OfType<TextEntity>().Where(t => t.Layer.Name == AlarmPlanDxfWriter.SummaryLayer).ShouldHaveSingleItem().Value.ShouldContain("2 шт.");
        // Только замечания раскладки АПС с местом на плане.
        doc.Entities.OfType<Circle>().Count(c => c.Layer.Name == AlarmPlanDxfWriter.IssueLayer).ShouldBe(1);
    }

    [Fact]
    public void With_a_stamp_the_plan_becomes_a_framed_sheet_with_legend_and_notes()
    {
        var outline = new Polygon([new(0, 0), new(20000, 0), new(20000, 10000), new(0, 10000)]);
        var zone = new DetectorZone("R1", "Стоянка", outline, new Length(3), DetectorKind.Smoke, DetectionAlgorithm.C, true);
        var layout = new DetectorLayout([new PlacedDetector("R1.D1", "R1", new PlanPoint(5000, 5000), DetectorKind.Smoke)], new Dictionary<string, int> { ["R1"] = 1 }, []);
        var stamp = new TitleBlock("123-АПС", "Автостоянка", "", "План расстановки извещателей", "Р", "Организация", [new("Разраб.", "Иванов")]);

        var doc = AlarmPlanDxfWriter.Build(layout, [zone], [new Issue("aps.layout.wallOffset", IssueLevel.Info, "узко", "R1", Location: new PlanPoint(1000, 1000))], manuals: [new PlacedManual("R1.M1", "R1", new PlanPoint(0, 5000))], stamp: stamp);

        doc.Entities.ShouldContain(e => e.Layer.Name == GostSheet.Layer);
        var texts = doc.Entities.OfType<TextEntity>().Select(t => t.Value).Concat(doc.Entities.OfType<MText>().Select(t => t.Value)).ToList();
        texts.ShouldContain("Условные обозначения");
        texts.ShouldContain(t => t.StartsWith("Примечания:", StringComparison.Ordinal) && t.Contains("Стоянка: дымовые ИП"));
        texts.ShouldContain("1"); // номер метки замечания
        // План 20 × 10 м — на А3 в 1:100: тексты высотой 250 мм модели.
        doc.Entities.OfType<TextEntity>().Where(t => t.Value == "Условные обозначения").Single().Height.ShouldBe(250, 1e-9);
    }
}
