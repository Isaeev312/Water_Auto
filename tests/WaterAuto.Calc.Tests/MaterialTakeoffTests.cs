using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class MaterialTakeoffTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);
    private static readonly SprinklerSpec Sprinkler = new("Ороситель спринклерный водяной", "СВН-К115");

    // Ввод → поворот → тройник на питающем → ряд из трёх оросителей: DN 70 (нет в ГОСТ 3262) → 50 → 25.
    private static readonly PipeRoute Route = new(
        "R1",
        "I1",
        [
            new RouteNode("I1", RouteNodeKind.Inlet, new(0, 0)),
            new RouteNode("B1", RouteNodeKind.Bend, new(0, 1000)),
            new RouteNode("T1", RouteNodeKind.Tee, new(1000, 1000)),
            new RouteNode("S1", RouteNodeKind.Sprinkler, new(2000, 1000), "S1"),
            new RouteNode("S2", RouteNodeKind.Sprinkler, new(5000, 1000), "S2"),
            new RouteNode("S3", RouteNodeKind.Sprinkler, new(1000, 2000), "S3"),
        ],
        [
            new RouteSegment("P1", "I1", "B1", RouteSegmentRole.Supply, new Length(1.0), 3, 70),
            new RouteSegment("P2", "B1", "T1", RouteSegmentRole.Supply, new Length(1.0), 3, 70),
            new RouteSegment("P3", "T1", "S1", RouteSegmentRole.Row, new Length(1.0), 2, 50),
            new RouteSegment("P4", "S1", "S2", RouteSegmentRole.Row, new Length(3.0), 1, 25),
            new RouteSegment("P5", "T1", "S3", RouteSegmentRole.Main, new Length(1.0), 1, 25),
        ]);

    private static MaterialTakeoff Takeoff(int sprinklers = 3) =>
        MaterialTakeoffBuilder.Build([Route], sprinklers, Norms, "ГОСТ 3262", Sprinkler);

    private static double Qty(MaterialTakeoff t, TakeoffGroup g, string name) =>
        t.Items.Where(i => i.Group == g && i.Name.Contains(name, StringComparison.Ordinal)).Sum(i => i.Quantity);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(100, 5)]
    [InlineData(101, 10)]
    [InlineData(1000, 10)]
    [InlineData(1001, 15)]
    public void Spare_sprinklers_follow_6_1_15(int installed, int spare)
    {
        MaterialTakeoffBuilder.SpareSprinklers(Norms, installed).ShouldBe(spare);
    }

    [Fact]
    public void Sprinklers_and_spare_are_separate_lines()
    {
        var t = Takeoff();
        var lines = t.Items.Where(i => i.Group == TakeoffGroup.Sprinklers).ToList();
        lines.Count.ShouldBe(2);
        lines[0].Quantity.ShouldBe(3);
        lines[0].Designation.ShouldBe("СВН-К115");
        lines[1].Quantity.ShouldBe(5);
        lines[1].Note!.ShouldContain("6.1.15");
    }

    [Fact]
    public void Pipes_are_summed_by_the_DN_of_the_series()
    {
        // DN 70 → DN 80 ГОСТ 3262 (88,5 × 3,5); DN 25 — из двух участков.
        var t = Takeoff();
        var pipes = t.Items.Where(i => i.Group == TakeoffGroup.Pipes).ToList();
        pipes.Select(p => p.Name).ShouldBe(["Трубы стальные водогазопроводные DN25, Ø33,5×2,8 мм", "Трубы стальные водогазопроводные DN50, Ø60×3 мм", "Трубы стальные водогазопроводные DN80, Ø88,5×3,5 мм"]);
        pipes.Select(p => p.Quantity).ShouldBe([4.0, 1.0, 2.0]);
        pipes.ShouldAllBe(p => p.Unit == "м" && p.Designation == "ГОСТ 3262");
        t.Issues.ShouldContain(i => i.Code == "network.dnSubstituted");
    }

    [Fact]
    public void Fittings_are_counted_from_the_route_geometry()
    {
        var t = Takeoff();
        Qty(t, TakeoffGroup.Fittings, "Отвод 90° DN80").ShouldBe(1); // поворот подводящего
        Qty(t, TakeoffGroup.Fittings, "Тройник DN80×25").ShouldBe(1); // T1: дальше DN 50 и DN 25
        Qty(t, TakeoffGroup.Fittings, "Тройник под ороситель DN50").ShouldBe(1); // S1 в середине ряда
        Qty(t, TakeoffGroup.Fittings, "Отвод под ороситель DN25").ShouldBe(2); // концы S2 и S3
        Qty(t, TakeoffGroup.Fittings, "Переход DN50×25").ShouldBe(1); // после S1 ряд сужается
        Qty(t, TakeoffGroup.Fittings, "Переход DN80×50").ShouldBe(1); // прямо через T1 ряд сужается
    }

    [Fact]
    public void Routed_room_takeoff_matches_the_route()
    {
        var rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var layout = SprinklerLayouter.Layout(plan, rules);
        var route = PipeRouter.Route(plan, layout, Norms, rules, new Pressure(0.3)).Routes[0];
        var t = MaterialTakeoffBuilder.Build([route], layout.Sprinklers.Count, Norms, "ГОСТ 3262", Sprinkler);

        t.Items.Where(i => i.Group == TakeoffGroup.Pipes).Sum(i => i.Quantity).ShouldBe(route.Segments.Sum(s => s.Length.Meters), 0.01);
        t.Items.Where(i => i.Group == TakeoffGroup.Fittings && i.Name.Contains("под ороситель", StringComparison.Ordinal)).Sum(i => i.Quantity).ShouldBe(54);
        t.Items.First(i => i.Group == TakeoffGroup.Sprinklers).Quantity.ShouldBe(54);
        t.Items.Where(i => i.Group == TakeoffGroup.Sprinklers).Last().Quantity.ShouldBe(5);

        // Фасонные части одного вида — по возрастанию DN, а не по тексту (DN80 раньше DN100).
        var reducers = t.Items.Where(i => i.Name.StartsWith("Переход", StringComparison.Ordinal)).Select(i => i.Name).ToList();
        reducers.ShouldBe(reducers.OrderBy(n => int.Parse(n.Split("DN")[1].Split('×')[0], System.Globalization.CultureInfo.InvariantCulture)).ToList());
    }
}
