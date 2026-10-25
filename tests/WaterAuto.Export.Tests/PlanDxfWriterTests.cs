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
