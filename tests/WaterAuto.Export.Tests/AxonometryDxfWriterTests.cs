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
        labels.ShouldContain("DN32"); // рядок — только диаметр
        labels.ShouldContain(t => t.StartsWith("DN50 L=", StringComparison.Ordinal));
        On(doc, AxonometryDxfWriter.MarkLayer).OfType<TextEntity>().Select(t => t.Value).ShouldContain("отм. −1,500");
        On(doc, AxonometryDxfWriter.LabelLayer).OfType<MText>().Select(t => t.Value).ShouldContain("Ввод DN50");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sprinklers_sit_on_stubs_up_or_down_by_deflector(bool up)
    {
        var doc = AxonometryDxfWriter.Build([Route], Elevation, new AxonometryStyle(SprinklersUp: up));
        var heads = On(doc, AxonometryDxfWriter.SprinklerLayer).OfType<Circle>().ToList();
        heads.Count.ShouldBe(3);
        var s1 = AxonometryDxfWriter.Project(new PlanPoint(4000, 0), -1500, new AxonometryStyle());
        var head = heads.OrderBy(c => Math.Abs(c.Center.X - s1.X)).First();
        (head.Center.Y - s1.Y).ShouldBe(up ? 240 : -240, 1e-6); // отвод 3 мм листа при высоте подписей 200 мм (1:80)
        On(doc, AxonometryDxfWriter.SprinklerLayer).OfType<Line>().Count().ShouldBe(3);
    }

    [Fact]
    public void Calculation_direction_point_numbers_and_zone_are_shown()
    {
        var calc = new AxonometryCalc(
            "R1",
            "I1",
            new Dictionary<string, string> { ["S2"] = "1", ["S1"] = "2", ["T1"] = "а", ["I1"] = "ВВ" },
            ["P1", "P2", "P3"],
            ["S1", "S2"]);
        var doc = AxonometryDxfWriter.Build([Route], Elevation, new AxonometryStyle(), calc: calc);
        On(doc, AxonometryDxfWriter.PathLayer).OfType<Line>().Count().ShouldBe(3);
        On(doc, AxonometryDxfWriter.PipeLayer).OfType<Line>().Count().ShouldBe(2); // P4 и стояк
        var zone = On(doc, AxonometryDxfWriter.ZoneLayer);
        zone.OfType<MText>().Select(t => t.Value).ShouldBe(["1", "2", "а", "ВВ"], ignoreOrder: true);
        zone.OfType<LwPolyline>().Count().ShouldBe(2); // залитые круги у двух оросителей зоны
    }

    [Fact]
    public void Every_diameter_change_is_labelled_without_overlaps()
    {
        // Рядок, где диаметр меняется на каждом участке длиной 0,4 м: подпись «DN25» (≈ 0,56 м) шире участка.
        int[] dns = [50, 40, 32, 25, 32, 40, 50, 40, 32, 25];
        var nodes = new List<RouteNode> { new("I1", RouteNodeKind.Inlet, new(0, 0)) };
        var segments = new List<RouteSegment>();
        for (var k = 0; k < dns.Length; k++)
        {
            nodes.Add(new RouteNode($"N{k}", RouteNodeKind.Tee, new((k + 1) * 400, 0)));
            segments.Add(new RouteSegment($"P{k}", k == 0 ? "I1" : $"N{k - 1}", $"N{k}", RouteSegmentRole.Row, new Length(0.4), 1, dns[k]));
        }

        var doc = AxonometryDxfWriter.Build([new PipeRoute("R", "I1", nodes, segments)], new Length(0), new AxonometryStyle());
        var boxes = On(doc, AxonometryDxfWriter.LabelLayer).OfType<TextEntity>()
            .Select(t => (X0: t.InsertPoint.X, X1: t.InsertPoint.X + (0.7 * t.Height * t.Value.Length), Y0: t.InsertPoint.Y, Y1: t.InsertPoint.Y + t.Height))
            .ToList();
        boxes.Count.ShouldBe(dns.Length); // каждая смена диаметра подписана — у трубы или на выноске
        for (var a = 0; a < boxes.Count; a++)
        {
            for (var b = a + 1; b < boxes.Count; b++)
            {
                var overlap = boxes[a].X0 < boxes[b].X1 - 1e-6 && boxes[b].X0 < boxes[a].X1 - 1e-6 && boxes[a].Y0 < boxes[b].Y1 - 1e-6 && boxes[b].Y0 < boxes[a].Y1 - 1e-6;
                overlap.ShouldBeFalse($"подписи {a} и {b} накладываются");
            }
        }
    }

    [Fact]
    public void Elevation_marks_at_the_inlet_and_the_far_main_end()
    {
        var doc = WriteAndRead();
        var marks = On(doc, AxonometryDxfWriter.MarkLayer).OfType<TextEntity>().Select(t => t.Value).ToList();
        marks.ShouldBe(["отм. −1,500", "−1,500"], ignoreOrder: true); // у ввода и в конце питающего P4
        On(doc, AxonometryDxfWriter.MarkLayer).OfType<Line>().Count().ShouldBe(8); // стрелка, выносная линия и полка × 2
    }

    [Fact]
    public void Smaller_pipe_is_broken_where_pipes_cross_on_the_scheme()
    {
        // Рядок DN25 пересекает питающий DN50 в точке (2000, 1000) без общего узла.
        var route = new PipeRoute(
            "R2",
            "I1",
            [
                new RouteNode("I1", RouteNodeKind.Inlet, new(2000, 0)),
                new RouteNode("M1", RouteNodeKind.Tee, new(2000, 2000)),
                new RouteNode("T2", RouteNodeKind.Tee, new(0, 1000)),
                new RouteNode("S1", RouteNodeKind.Sprinkler, new(4000, 1000), "S1"),
            ],
            [
                new RouteSegment("P1", "I1", "M1", RouteSegmentRole.Main, new Length(2.0), 1, 50),
                new RouteSegment("P2", "M1", "T2", RouteSegmentRole.Main, new Length(2.2), 1, 50),
                new RouteSegment("P3", "T2", "S1", RouteSegmentRole.Row, new Length(4.0), 1, 25),
            ]);
        var doc = AxonometryDxfWriter.Build([route], new Length(0), new AxonometryStyle());
        var pipes = On(doc, AxonometryDxfWriter.PipeLayer).OfType<Line>().ToList();
        pipes.Count.ShouldBe(5); // P1, P2, рядок из двух частей, стояк
        var row = pipes.Where(l => Math.Abs(l.StartPoint.Y - l.EndPoint.Y) < 1e-6 && l.StartPoint.Y > 0).ToList();
        row.Count.ShouldBe(2);
        var p1 = pipes.Single(l => l.StartPoint.X < l.EndPoint.X - 1 && l.EndPoint.Y > l.StartPoint.Y + 1000);
        (p1.EndPoint.Y - p1.StartPoint.Y).ShouldBe(2000 * Math.Sin(Math.PI / 4), 1e-6); // питающий — без разрыва
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

    [Fact]
    public void Main_of_one_diameter_gets_one_label_with_total_length()
    {
        // Питающий DN65 из трёх участков по 3 м вдоль Y, от каждого узла — короткий рядок DN25.
        var nodes = new List<RouteNode> { new("I1", RouteNodeKind.Inlet, new(0, 0)) };
        var segments = new List<RouteSegment>();
        for (var k = 0; k < 3; k++)
        {
            nodes.Add(new RouteNode($"M{k}", RouteNodeKind.Tee, new(0, (k + 1) * 3000)));
            nodes.Add(new RouteNode($"S{k}", RouteNodeKind.Sprinkler, new(3000, (k + 1) * 3000), $"S{k}"));
            segments.Add(new RouteSegment($"P{k}", k == 0 ? "I1" : $"M{k - 1}", $"M{k}", RouteSegmentRole.Main, new Length(3), 3 - k, 65));
            segments.Add(new RouteSegment($"R{k}", $"M{k}", $"S{k}", RouteSegmentRole.Row, new Length(3), 1, 25));
        }

        var doc = AxonometryDxfWriter.Build([new PipeRoute("R", "I1", nodes, segments)], new Length(0), new AxonometryStyle());
        var texts = On(doc, AxonometryDxfWriter.LabelLayer).OfType<TextEntity>().Select(t => t.Value).ToList();
        texts.Count(t => t.StartsWith("DN65", StringComparison.Ordinal)).ShouldBe(1);
        texts.ShouldContain("DN65 L=9,00 м");
    }
}
