using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

public class InletRoutingTests
{
    // Помещение 20 × 10 м, в нём насосная 15…20 × 3…7 м, колонна 400 × 400 в центре (7500, 5000).
    private static FloorPlan Plan()
    {
        var room = new Room("R1", "Автостоянка", new Polygon([new(0, 0), new(20000, 0), new(20000, 10000), new(0, 10000)]));
        var col = new Column("C1", new Polygon([new(7300, 4800), new(7700, 4800), new(7700, 5200), new(7300, 5200)]));
        return new FloorPlan([room], [col], [], [], [], [])
        {
            PumpRooms = [new PumpRoom("P", new Polygon([new(15000, 3000), new(20000, 3000), new(20000, 7000), new(15000, 7000)]))],
        };
    }

    private static double Gap(PlanPoint a, PlanPoint b)
    {
        var (x0, x1, y0, y1) = (Math.Min(a.XMm, b.XMm), Math.Max(a.XMm, b.XMm), Math.Min(a.YMm, b.YMm), Math.Max(a.YMm, b.YMm));
        var dx = Math.Max(0, Math.Max(7300 - x1, x0 - 7700));
        var dy = Math.Max(0, Math.Max(4800 - y1, y0 - 5200));
        return Math.Max(dx, dy);
    }

    [Fact]
    public void Route_goes_around_the_column_with_right_angles_to_the_pump_room_wall()
    {
        var r = InletRouting.Route(Plan(), new PlanPoint(0, 5000), new PlanPoint(15000, 5000), clearanceMm: 300);
        r.Issues.ShouldBeEmpty();
        r.Path[0].ShouldBe(new PlanPoint(0, 5000));
        r.Path[^1].ShouldBe(new PlanPoint(15000, 5000));
        r.Path.Zip(r.Path.Skip(1)).ShouldAllBe(s => Math.Abs(s.First.XMm - s.Second.XMm) < 1e-6 || Math.Abs(s.First.YMm - s.Second.YMm) < 1e-6);

        // Ни один участок не проходит ближе 300 мм к колонне.
        r.Path.Zip(r.Path.Skip(1)).ShouldAllBe(s => Gap(s.First, s.Second) >= 300 - 1e-6);
        r.Elbows.ShouldBeLessThanOrEqualTo(4);
        r.LengthMm.ShouldBeGreaterThan(15000);
    }

    [Fact]
    public void Target_inside_the_pump_room_is_an_issue()
    {
        var r = InletRouting.Route(Plan(), new PlanPoint(0, 5000), new PlanPoint(17000, 5000), clearanceMm: 300);
        r.Issues.ShouldContain(i => i.Code == "inlet.noRoute");
    }

    [Fact]
    public void Second_route_does_not_cross_the_first()
    {
        var first = InletRouting.Route(Plan(), new PlanPoint(0, 5000), new PlanPoint(15000, 5000), 300);
        var second = InletRouting.Route(Plan(), new PlanPoint(10000, 10000), new PlanPoint(15000, 4000), 300, [first.Path]);
        second.Issues.ShouldBeEmpty();
        var crossings = first.Path.Zip(first.Path.Skip(1)).SelectMany(a => second.Path.Zip(second.Path.Skip(1)).Select(b => (a, b)))
            .Count(x => Intersects(x.a.First, x.a.Second, x.b.First, x.b.Second));
        crossings.ShouldBe(0);
    }

    private static bool Intersects(PlanPoint a, PlanPoint b, PlanPoint c, PlanPoint d)
    {
        double rx = b.XMm - a.XMm, ry = b.YMm - a.YMm, sx = d.XMm - c.XMm, sy = d.YMm - c.YMm;
        var den = (rx * sy) - (ry * sx);
        if (Math.Abs(den) < 1e-9)
        {
            return false;
        }

        var t = (((c.XMm - a.XMm) * sy) - ((c.YMm - a.YMm) * sx)) / den;
        var u = (((c.XMm - a.XMm) * ry) - ((c.YMm - a.YMm) * rx)) / den;
        return t > 0 && t < 1 && u > 0 && u < 1;
    }
}
