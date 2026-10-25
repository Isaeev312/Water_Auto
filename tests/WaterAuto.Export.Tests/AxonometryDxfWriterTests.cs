using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using WaterAuto.Core;

namespace WaterAuto.Export.Tests;

public class AxonometryDxfWriterTests
{
    // Ввод → тройник → два оросителя по X и один по Y (на плане), всё на отметке −1,500.
    private static readonly PipeRoute Route = new(
        "R1",
        "I1",
        [
            new RouteNode("I1", RouteNodeKind.Inlet, new(0, 0)),
            new RouteNode("T1", RouteNodeKind.Tee, new(1000, 0)),
            new RouteNode("S1", RouteNodeKind.Sprinkler, new(4000, 0), "S1"),
            new RouteNode("S2", RouteNodeKind.Sprinkler, new(7000, 0), "S2"),
            new RouteNode("S3", RouteNodeKind.Sprinkler, new(1000, 3000), "S3"),
        ],
        [
            new RouteSegment("P1", "I1", "T1", RouteSegmentRole.Supply, new Length(1.0), 3, 50),
            new RouteSegment("P2", "T1", "S1", RouteSegmentRole.Row, new Length(3.0), 2, 32),
            new RouteSegment("P3", "S1", "S2", RouteSegmentRole.Row, new Length(3.0), 1, 32),
            new RouteSegment("P4", "T1", "S3", RouteSegmentRole.Main, new Length(3.0), 1, 25),
        ]);

    private static readonly Length Elevation = new(-1.5);

    private static CadDocument WriteAndRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.dxf");
        AxonometryDxfWriter.Write(path, [Route], Elevation);
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

    [Theory]
    [InlineData(1000, 0, 0, 1000, 0)] // ось X — без искажения, горизонтально
    [InlineData(0, 1000, 0, 707.107, 707.107)] // ось Y — под 45°, без искажения
    [InlineData(0, 0, -1500, 0, -1500)] // ось Z — вертикально
    public void Frontal_isometric_projection(double x, double y, double z, double px, double py)
    {
        var p = AxonometryDxfWriter.Project(new PlanPoint(x, y), z, new AxonometryStyle());
        p.X.ShouldBe(px, 0.01);
        p.Y.ShouldBe(py, 0.01);
    }

    [Fact]
    public void Every_segment_is_a_projected_line()
    {
        var doc = WriteAndRead();
        var lines = On(doc, AxonometryDxfWriter.PipeLayer).OfType<Line>().ToList();
        lines.Count(l => l.StartPoint.Y != l.EndPoint.Y || l.StartPoint.X != l.EndPoint.X).ShouldBeGreaterThanOrEqualTo(4);

        // P4 идёт по Y плана: на аксонометрии — под 45° из тройника.
        lines.ShouldContain(l => Math.Abs(l.StartPoint.X - 1000) < 0.01 && Math.Abs(l.StartPoint.Y + 1500) < 0.01
            && Math.Abs(l.EndPoint.X - (1000 + 2121.32)) < 0.01 && Math.Abs(l.EndPoint.Y - (-1500 + 2121.32)) < 0.01);
    }

    [Fact]
    public void Sprinklers_are_marked_and_diameters_labelled()
    {
        var doc = WriteAndRead();
        On(doc, AxonometryDxfWriter.SprinklerLayer).OfType<Circle>().Count().ShouldBe(3);
        var labels = On(doc, AxonometryDxfWriter.LabelLayer).OfType<TextEntity>().Select(t => t.Value).ToList();
        labels.Count(t => t.StartsWith("DN", StringComparison.Ordinal)).ShouldBe(3); // P1 подводящий, P2 — смена DN, P4 питающий; P3 без подписи
        labels.ShouldContain(t => t.Contains("отм. −1,500", StringComparison.Ordinal));
    }

    [Fact]
    public void Riser_is_drawn_down_from_the_inlet()
    {
        var doc = WriteAndRead();
        var riser = On(doc, AxonometryDxfWriter.PipeLayer).OfType<Line>()
            .Single(l => Math.Abs(l.StartPoint.X - l.EndPoint.X) < 0.01 && Math.Abs(l.StartPoint.X) < 0.01);
        Math.Max(riser.StartPoint.Y, riser.EndPoint.Y).ShouldBe(-1500, 0.01);
        Math.Min(riser.StartPoint.Y, riser.EndPoint.Y).ShouldBeLessThan(-1500);
    }
}
