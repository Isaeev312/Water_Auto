using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class PipeSupportsTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    // Ввод → тройник → рядок DN25 из 4 оросителей с шагом 3 м (прямая длиной 1 + 9 м).
    private static PipeRoute Row(int dn = 25) => new(
        "R1",
        "I1",
        [
            new RouteNode("I1", RouteNodeKind.Inlet, new(0, 0)),
            new RouteNode("T1", RouteNodeKind.Tee, new(0, 1000)),
            new RouteNode("S1", RouteNodeKind.Sprinkler, new(1000, 1000), "S1"),
            new RouteNode("S2", RouteNodeKind.Sprinkler, new(4000, 1000), "S2"),
            new RouteNode("S3", RouteNodeKind.Sprinkler, new(7000, 1000), "S3"),
            new RouteNode("S4", RouteNodeKind.Sprinkler, new(10000, 1000), "S4"),
        ],
        [
            new RouteSegment("P1", "I1", "T1", RouteSegmentRole.Supply, new Length(1), 4, 50),
            new RouteSegment("P2", "T1", "S1", RouteSegmentRole.Row, new Length(1), 4, dn),
            new RouteSegment("P3", "S1", "S2", RouteSegmentRole.Row, new Length(3), 3, dn),
            new RouteSegment("P4", "S2", "S3", RouteSegmentRole.Row, new Length(3), 2, dn),
            new RouteSegment("P5", "S3", "S4", RouteSegmentRole.Row, new Length(3), 1, dn),
        ]);

    [Fact]
    public void Spacing_follows_table_6_7_1_by_the_nearest_smaller_outer_diameter()
    {
        // DN25 по ГОСТ 3262 — Ø33,5: в табл. 6.7.1 ближайший меньший — 32 мм, шаг не более 3,5 м.
        var r = PipeSupports.Build([Row()], Norms, "ГОСТ 3262");
        r.MaxSpacing(25).ShouldBe(3.5);
        r.MaxSpacing(50).ShouldBe(5); // Ø60 → 57 мм
    }

    [Fact]
    public void Straight_row_gets_supports_not_farther_than_the_table_spacing()
    {
        var r = PipeSupports.Build([Row()], Norms, "ГОСТ 3262");
        var row = r.Points.Where(p => p.Dn == 25).OrderBy(p => p.At.XMm).ToList();
        row.Count.ShouldBe(4); // 10 м / 3,5 м → 3 пролёта, 4 опоры
        row.Zip(row.Skip(1), (a, b) => b.At.XMm - a.At.XMm).ShouldAllBe(d => d <= 3500 + 1e-6);
    }

    [Fact]
    public void Support_near_the_last_sprinkler_keeps_6_7_1_26_and_joint_distance()
    {
        // До последнего оросителя DN25 — не более 0,9 м (6.7.1.26) и не ближе 0,2 м к соединению (6.7.1.31).
        var r = PipeSupports.Build([Row()], Norms, "ГОСТ 3262");
        var last = r.Points.Where(p => p.Dn == 25).Max(p => p.At.XMm);
        (10000 - last).ShouldBeInRange(200, 900);
        r.Issues.ShouldBeEmpty();
    }
}
