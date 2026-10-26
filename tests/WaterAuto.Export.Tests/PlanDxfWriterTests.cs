using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using WaterAuto.Core;

namespace WaterAuto.Export.Tests;

public class PlanDxfWriterTests
{
    // Помещение с двумя оросителями: ввод → тройник → ороситель S1 → ороситель S2.
    private static readonly SprinklerLayout Layout = new(
        [new PlacedSprinkler("R1.S1", "R1", new(2000, 1500)), new PlacedSprinkler("R1.S2", "R1", new(5000, 1500))],
        [new LayoutZone("R1", new Polygon([new(0, 0), new(6000, 0), new(6000, 3000), new(0, 3000)]), 2, 0)],
        []);

    private static readonly PipeRoute Route = new(
        "R1",
        "I1",
        [
            new RouteNode("I1", RouteNodeKind.Inlet, new(500, 500)),
            new RouteNode("R1.T1", RouteNodeKind.Tee, new(500, 1500)),
            new RouteNode("R1.S1", RouteNodeKind.Sprinkler, new(2000, 1500), "R1.S1"),
            new RouteNode("R1.S2", RouteNodeKind.Sprinkler, new(5000, 1500), "R1.S2"),
        ],
        [
            new RouteSegment("R1.P1", "I1", "R1.T1", RouteSegmentRole.Supply, new Length(1.0), 2, 32),
            new RouteSegment("R1.P2", "R1.T1", "R1.S1", RouteSegmentRole.Row, new Length(1.5), 2, 32),
            new RouteSegment("R1.P3", "R1.S1", "R1.S2", RouteSegmentRole.Row, new Length(3.0), 1, 25),
        ]);

    private static readonly Issue Located = new("routing.throughColumn", IssueLevel.Warning, "Трубопровод проходит через колонну.", "C1", Location: new PlanPoint(3500, 1500));
    private static readonly Issue Unlocated = new("routing.noDnTable", IssueLevel.Error, "Нет табл. Б.4.");

    private static CadDocument WriteAndRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.dxf");
        PlanDxfWriter.Write(path, Layout, [Route], [Located, Unlocated]);
        try
        {
            return DxfReader.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<Entity> On(CadDocument doc, string layer) => doc.Entities.Where(e => e.Layer.Name == layer).ToList();

    [Fact]
    public void Dwg_extension_writes_a_dwg_for_AutoCAD_insert()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.dwg");
        PlanDxfWriter.Write(path, Layout, [Route], [Located]);
        try
        {
            var doc = DwgReader.Read(path);
            On(doc, PlanDxfWriter.SprinklerLayer).OfType<Insert>().Count().ShouldBe(2);
            On(doc, PlanDxfWriter.PipeLayer).OfType<Line>().Count().ShouldBe(3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly PlanCalcSummary Summary = new(
        "Сводка гидравлического расчёта",
        [("Расход насоса Q_нас", "33,77 л/с (Б.26)"), ("Требуемый напор H_н", "35,4 м (Б.30)")],
        ["R1.S1", "R1.S2"],
        "Диктующая зона: S = 120 м², N = 2",
        ZoneMarginMm: 500);

    [Fact]
    public void Dictating_zone_is_outlined_around_its_sprinklers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.dxf");
        PlanDxfWriter.Write(path, Layout, [Route], [], summary: Summary);
        var doc = DxfReader.Read(path);
        File.Delete(path);
        var zone = On(doc, PlanDxfWriter.ZoneLayer);
        var outline = zone.OfType<LwPolyline>().ShouldHaveSingleItem();
        outline.IsClosed.ShouldBeTrue();
        var xs = outline.Vertices.Select(v => v.Location.X).ToList();
        var ys = outline.Vertices.Select(v => v.Location.Y).ToList();
        (xs.Min(), xs.Max(), ys.Min(), ys.Max()).ShouldBe((1500.0, 5500.0, 1000.0, 2000.0)); // оросители ± 500 мм
        zone.OfType<Circle>().Count().ShouldBe(2); // выделены оросители зоны
        zone.OfType<MText>().ShouldContain(t => t.Value.Contains("S = 120 м²", StringComparison.Ordinal));
    }

    [Fact]
    public void Calculation_summary_is_a_table_on_its_own_layer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.dxf");
        PlanDxfWriter.Write(path, Layout, [Route], [], summary: Summary);
        var doc = DxfReader.Read(path);
        File.Delete(path);
        var text = On(doc, PlanDxfWriter.SummaryLayer).OfType<MText>().ShouldHaveSingleItem().Value;
        text.ShouldContain("Сводка гидравлического расчёта");
        text.ShouldContain("Расход насоса Q_нас — 33,77 л/с (Б.26)");
        text.ShouldContain("Требуемый напор H_н — 35,4 м (Б.30)");
    }

    [Fact]
    public void Without_summary_the_new_layers_stay_empty()
    {
        var doc = WriteAndRead();
        On(doc, PlanDxfWriter.ZoneLayer).ShouldBeEmpty();
        On(doc, PlanDxfWriter.SummaryLayer).ShouldBeEmpty();
    }

    [Fact]
    public void Plan_is_drawn_over_the_source_drawing_without_restyling_it()
    {
        var source = new CadDocument(ACadVersion.AC1032);
        source.Header.InsUnits = UnitsType.Millimeters;
        var walls = new ACadSharp.Tables.Layer("АР_СТЕНЫ");
        source.Layers.Add(walls);
        source.Entities.Add(new Line(new CSMath.XYZ(0, 0, 0), new CSMath.XYZ(6000, 0, 0)) { Layer = walls });
        source.Entities.Add(new TextEntity { Value = "Стоянка", Height = 300, Layer = walls });
        var basePath = Path.Combine(Path.GetTempPath(), $"wa-base-{Guid.NewGuid():N}.dxf");
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.dwg");
        DxfWriter.Write(basePath, source, false);
        try
        {
            PlanDxfWriter.Write(path, Layout, [Route], [], basePath: basePath);
            var doc = DwgReader.Read(path);
            doc.Entities.OfType<Line>().ShouldContain(l => l.Layer.Name == "АР_СТЕНЫ");
            doc.Entities.OfType<Line>().ShouldContain(l => l.Layer.Name == PlanDxfWriter.PipeLayer);
            doc.Entities.OfType<TextEntity>().Single(x => x.Value == "Стоянка").Style.Filename.ShouldNotBe("GOST Common.ttf");
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(path);
        }
    }

    [Fact]
    public void Inlet_gets_a_leader_with_DN_elevation_and_reference()
    {
        var doc = PlanDxfWriter.Build(Layout, [Route], [], new PlanDxfStyle(), inlets: [new PlanInletNote(new PlanPoint(500, 500), ["Ввод DN100", "отм. −1,500", "см. раздел НС"])]);
        var text = doc.Entities.OfType<MText>().Single(m => m.Value.Contains("см. раздел НС", StringComparison.Ordinal));
        text.Value.ShouldContain("DN100");
        text.Layer.Name.ShouldBe(PlanDxfWriter.SegmentLayer);
        doc.Entities.OfType<LwPolyline>().ShouldContain(l => l.Layer.Name == PlanDxfWriter.SegmentLayer && l.Vertices.First().Location.X == 500);
    }

    [Fact]
    public void Dimension_chain_has_a_text_per_span_with_its_length()
    {
        // Стена (0) → ороситель (2000) → ороситель (5000) → стена (6000), размерная линия на 600 мм ниже.
        var chain = new PlanDimensionChain([new(0, 1500), new(2000, 1500), new(5000, 1500), new(6000, 1500)], -600);
        var doc = PlanDxfWriter.Build(Layout, [Route], [], new PlanDxfStyle(), dimensions: [chain]);
        var texts = doc.Entities.OfType<TextEntity>().Where(x => x.Layer.Name == PlanDxfWriter.DimensionLayer).Select(x => x.Value).ToList();
        texts.ShouldBe(["2000", "3000", "1000"]);
        doc.Entities.OfType<Line>().Count(l => l.Layer.Name == PlanDxfWriter.DimensionLayer).ShouldBeGreaterThan(4);
    }

    [Fact]
    public void Pipes_are_0_40_mm_and_texts_use_GOST_Common()
    {
        var doc = WriteAndRead();
        doc.Layers[PlanDxfWriter.PipeLayer].LineWeight.ShouldBe(LineWeightType.W40);
        var texts = doc.Entities.OfType<TextEntity>().Select(x => x.Style).Concat(doc.Entities.OfType<MText>().Select(x => x.Style)).ToList();
        texts.ShouldNotBeEmpty();
        texts.ShouldAllBe(s => s.Filename == "GOST Common.ttf");
    }

    [Fact]
    public void Drawing_is_in_millimetres_with_the_output_layers()
    {
        var doc = WriteAndRead();
        doc.Header.InsUnits.ShouldBe(UnitsType.Millimeters);
        foreach (var layer in new[] { PlanDxfWriter.SprinklerLayer, PlanDxfWriter.PipeLayer, PlanDxfWriter.SegmentLayer, PlanDxfWriter.IssueLayer })
        {
            doc.Layers.Contains(layer).ShouldBeTrue(layer);
        }
    }

    [Fact]
    public void Sprinklers_are_block_inserts_at_their_points_with_ids()
    {
        var doc = WriteAndRead();
        var inserts = On(doc, PlanDxfWriter.SprinklerLayer).OfType<Insert>().ToList();
        inserts.Count.ShouldBe(2);
        inserts.Select(i => (i.InsertPoint.X, i.InsertPoint.Y)).ShouldBe([(2000.0, 1500.0), (5000.0, 1500.0)], ignoreOrder: true);
        inserts.ShouldAllBe(i => i.Block.Name == PlanDxfWriter.SprinklerBlock);
        inserts.Select(i => i.Attributes.Single(a => a.Tag == "ID").Value).ShouldBe(["R1.S1", "R1.S2"], ignoreOrder: true);
    }

    [Fact]
    public void Pipes_are_lines_between_route_nodes()
    {
        var doc = WriteAndRead();
        var lines = On(doc, PlanDxfWriter.PipeLayer).OfType<Line>().ToList();
        lines.Count.ShouldBe(3);
        lines.ShouldContain(l => l.StartPoint.X == 500 && l.StartPoint.Y == 500 && l.EndPoint.X == 500 && l.EndPoint.Y == 1500);
        lines.ShouldContain(l => l.StartPoint.X == 2000 && l.EndPoint.X == 5000 && l.StartPoint.Y == 1500);
    }

    [Fact]
    public void Diameters_are_labelled_on_the_supply_and_where_they_change()
    {
        // P1 — подводящий, P2 — DN как у P1 (без подписи), P3 — DN меньше (подпись).
        var doc = WriteAndRead();
        var labels = On(doc, PlanDxfWriter.SegmentLayer).OfType<TextEntity>().Select(t => t.Value).ToList();
        labels.Count.ShouldBe(2);
        labels.ShouldContain(t => t.Contains("DN32") && t.Contains("1,00"));
        labels.ShouldContain(t => t.Contains("DN25") && t.Contains("3,00"));
    }

    [Fact]
    public void Located_issues_are_marked_and_the_rest_are_listed()
    {
        var doc = WriteAndRead();
        var marks = On(doc, PlanDxfWriter.IssueLayer);
        marks.OfType<Circle>().ShouldHaveSingleItem().Center.X.ShouldBe(3500);
        var texts = marks.OfType<MText>().Select(t => t.Value).ToList();
        texts.ShouldContain(t => t.Contains("routing.throughColumn"));
        texts.ShouldContain(t => t.Contains("routing.noDnTable") && t.Contains("Нет табл. Б.4."));
    }
}
