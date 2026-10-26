using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

public class HydrantPlacementTests
{
    private static Polygon Rect(double x0, double y0, double x1, double y1) =>
        new([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)]);

    private static FloorPlan Plan(Polygon contour, params NoSprinklerZone[] zones) =>
        new([new Room("R1", "Автостоянка", contour)], [], [], [], [.. zones], []);

    // Независимая проверка: точка сетки видна не менее чем из jets кранов в пределах радиуса.
    private static int Covered(PlanPoint p, IReadOnlyList<PlanPoint> hydrants, double reachMm) =>
        hydrants.Count(h => Math.Sqrt(((h.XMm - p.XMm) * (h.XMm - p.XMm)) + ((h.YMm - p.YMm) * (h.YMm - p.YMm))) <= reachMm + 1e-6);

    [Fact]
    public void Every_point_of_a_hall_is_reached_by_two_jets()
    {
        var plan = Plan(Rect(0, 0, 40000, 20000));
        var r = HydrantPlacement.Place(plan, plan.Rooms[0], new Length(26), 2);
        r.Issues.ShouldNotContain(i => i.Level == IssueLevel.Warning);
        r.Points.Count.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var p in r.TestPoints)
        {
            Covered(p, r.Points, 26000).ShouldBeGreaterThanOrEqualTo(2);
        }
    }

    [Fact]
    public void Shorter_reach_needs_more_hydrants()
    {
        var plan = Plan(Rect(0, 0, 40000, 20000));
        var far = HydrantPlacement.Place(plan, plan.Rooms[0], new Length(30), 2).Points.Count;
        var near = HydrantPlacement.Place(plan, plan.Rooms[0], new Length(12), 2).Points.Count;
        near.ShouldBeGreaterThan(far);
    }

    [Fact]
    public void Hydrants_stand_inside_the_room_near_walls()
    {
        var plan = Plan(Rect(0, 0, 40000, 20000));
        var r = HydrantPlacement.Place(plan, plan.Rooms[0], new Length(26), 2);
        r.Points.ShouldAllBe(p => plan.Rooms[0].Contour.Contains(p));
        r.Points.ShouldAllBe(p => Math.Min(Math.Min(p.XMm, 40000 - p.XMm), Math.Min(p.YMm, 20000 - p.YMm)) < 1000);
    }

    [Fact]
    public void A_zone_blocks_the_jet_and_a_point_behind_it_needs_another_hydrant()
    {
        // Стена-зона поперёк зала с проходом сверху: точки справа не видны из левых кранов напрямую.
        var plan = Plan(Rect(0, 0, 40000, 20000), new NoSprinklerZone("Z1", Rect(19000, 0, 21000, 16000)));
        var r = HydrantPlacement.Place(plan, plan.Rooms[0], new Length(26), 2);
        r.Points.ShouldContain(p => p.XMm > 21000);
        r.Points.ShouldContain(p => p.XMm < 19000);
    }

    [Fact]
    public void Two_jets_come_from_two_different_risers_not_from_neighbouring_points()
    {
        // П. 7.14: по одной струе из двух пожарных стояков — краны не ставятся вплотную друг к другу.
        var plan = Plan(Rect(0, 0, 40000, 20000));
        var r = HydrantPlacement.Place(plan, plan.Rooms[0], new Length(26), 2);
        for (var a = 0; a < r.Points.Count; a++)
        {
            for (var b = a + 1; b < r.Points.Count; b++)
            {
                var d = Math.Sqrt(Math.Pow(r.Points[a].XMm - r.Points[b].XMm, 2) + Math.Pow(r.Points[a].YMm - r.Points[b].YMm, 2));
                d.ShouldBeGreaterThanOrEqualTo(HydrantPlacement.RiserSeparationMm);
            }
        }
    }
}
