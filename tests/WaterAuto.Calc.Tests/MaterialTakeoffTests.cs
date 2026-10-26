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

    [Fact]
    public void Fittings_carry_the_standard_of_their_type()
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
        var layout = SprinklerLayouter.Layout(plan, rules);
        var route = PipeRouter.Route(plan, layout, Norms, rules, new Pressure(0.3)).Routes[0];
        var takeoff = MaterialTakeoffBuilder.Build([route], layout.Sprinklers.Count, Norms, "ГОСТ 3262", new SprinklerSpec("Ороситель", "СВВ-12"), new Dictionary<string, string> { ["Отвод"] = "ГОСТ 17375-2001", ["Тройник"] = "ГОСТ 17376-2001", ["Переход"] = "ГОСТ 17378-2001" });
        var fittings = takeoff.Items.Where(i => i.Group == TakeoffGroup.Fittings).ToList();
        fittings.Where(i => i.Name.StartsWith("Отвод", StringComparison.Ordinal)).ShouldAllBe(i => i.Designation == "ГОСТ 17375-2001");
        fittings.Where(i => i.Name.StartsWith("Тройник", StringComparison.Ordinal)).ShouldAllBe(i => i.Designation == "ГОСТ 17376-2001");
        fittings.Where(i => i.Name.StartsWith("Переход", StringComparison.Ordinal)).ShouldAllBe(i => i.Designation == "ГОСТ 17378-2001");
        fittings.Where(i => i.Name.StartsWith("Крестовина", StringComparison.Ordinal)).ShouldAllBe(i => i.Designation == null);
    }

    [Fact]
    public void Ring_closing_segment_is_counted_in_pipe_lengths()
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
        var layout = SprinklerLayouter.Layout(plan, rules);
        var route = PipeRouter.Route(plan, layout, Norms, rules, new Pressure(0.3), ring: true).Routes[0];
        var takeoff = MaterialTakeoffBuilder.Build([route], layout.Sprinklers.Count, Norms, "ГОСТ 3262", new SprinklerSpec("Ороситель", "СВВ-12"));
        var meters = takeoff.Items.Where(i => i.Group == TakeoffGroup.Pipes).Sum(i => i.Quantity);
        meters.ShouldBe(route.Segments.Concat(route.Loops).Sum(s => s.Length.Meters), 0.05);
    }

    [Fact]
    public void Pipe_rows_carry_theoretical_mass_per_metre()
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
        var layout = SprinklerLayouter.Layout(plan, rules);
        var route = PipeRouter.Route(plan, layout, Norms, rules, new Pressure(0.3)).Routes[0];
        var takeoff = MaterialTakeoffBuilder.Build([route], layout.Sprinklers.Count, Norms, "ГОСТ 3262-75", new SprinklerSpec("Ороситель", "СВВ-12", "Спецавтоматика"), steelDensity: 7.85);
        var dn25 = takeoff.Items.Single(i => i.Group == TakeoffGroup.Pipes && i.Name.Contains("DN25", StringComparison.Ordinal));
        dn25.UnitMass!.Value.ShouldBe(Math.Round(Math.PI * (33.5 - 2.8) * 2.8 * 7.85 / 1000, 2));
        dn25.Designation.ShouldBe("ГОСТ 3262-75");
        takeoff.Items.First(i => i.Group == TakeoffGroup.Sprinklers).Supplier.ShouldBe("Спецавтоматика");
    }

    // Ввод → питающий DN80 до тройника T → два питающих: DN65 (дальше DN40 до тупика) и DN50 до тупика; на конце тупиков — по рядку.
    private static readonly PipeRoute Mains = new(
        "R2",
        "I1",
        [
            new RouteNode("I1", RouteNodeKind.Inlet, new(0, 0)),
            new RouteNode("T", RouteNodeKind.Tee, new(0, 1000)),
            new RouteNode("M1", RouteNodeKind.Tee, new(0, 5000)),
            new RouteNode("M2", RouteNodeKind.Tee, new(0, 9000)),
            new RouteNode("M3", RouteNodeKind.Tee, new(4000, 1000)),
            new RouteNode("S1", RouteNodeKind.Sprinkler, new(3000, 9000), "S1"),
            new RouteNode("S2", RouteNodeKind.Sprinkler, new(4000, 4000), "S2"),
        ],
        [
            new RouteSegment("A", "I1", "T", RouteSegmentRole.Supply, new Length(1.0), 2, 80),
            new RouteSegment("B", "T", "M1", RouteSegmentRole.Main, new Length(4.0), 1, 65),
            new RouteSegment("C", "M1", "M2", RouteSegmentRole.Main, new Length(4.0), 1, 40),
            new RouteSegment("D", "M2", "S1", RouteSegmentRole.Row, new Length(3.0), 1, 25),
            new RouteSegment("E", "T", "M3", RouteSegmentRole.Main, new Length(4.0), 1, 80),
            new RouteSegment("F", "M3", "S2", RouteSegmentRole.Row, new Length(3.0), 1, 25),
        ]);

    [Fact]
    public void Equal_tee_is_named_by_one_DN()
    {
        // T: вход DN80, выходы DN65 и DN80 — тройник DN80×65; M3 — поворот питающего в рядок, не тройник.
        var route = Mains with
        {
            Segments = [.. Mains.Segments.Select(s => s.Id == "B" ? s with { Dn = 80 } : s)],
        };
        var t = MaterialTakeoffBuilder.Build([route], 2, Norms, "ГОСТ 3262", Sprinkler);
        var names = t.Items.Where(i => i.Group == TakeoffGroup.Fittings).Select(i => i.Name).ToList();
        names.ShouldContain("Тройник DN80");
        names.ShouldNotContain("Тройник DN80×80");
    }

    [Fact]
    public void Fittings_are_grouped_by_kind_elbows_first()
    {
        var names = Takeoff().Items.Where(i => i.Group == TakeoffGroup.Fittings).Select(i => i.Name).ToList();
        int Kind(string n) => n.StartsWith("Отвод", StringComparison.Ordinal) ? 0 : n.StartsWith("Тройник", StringComparison.Ordinal) ? 1 : n.StartsWith("Крестовина", StringComparison.Ordinal) ? 2 : 3;
        names.Select(Kind).ShouldBe(names.Select(Kind).Order().ToList());
        names.IndexOf("Отвод под ороситель DN25").ShouldBeGreaterThan(names.IndexOf("Отвод 90° DN80"));
        names.IndexOf("Тройник под ороситель DN50").ShouldBeGreaterThan(names.IndexOf("Тройник DN80×25"));
    }

    [Fact]
    public void Flushing_devices_stand_at_dead_ends_of_mains_6_7_1_15()
    {
        // Тупики питающих: M2 (DN40 — меньше DN 50, промывка DN40) и M3 (DN80 — промывка DN 50).
        var points = FlushingPoints.Find([Mains], Norms);
        points.Select(p => (p.NodeId, p.PipeDn, p.Dn)).OrderBy(p => p.NodeId).ShouldBe([("M2", 40, 40), ("M3", 80, 50)]);
    }

    [Fact]
    public void Ring_gets_one_flushing_device_at_the_far_closure_end()
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
        var layout = SprinklerLayouter.Layout(plan, rules);
        var route = PipeRouter.Route(plan, layout, Norms, rules, new Pressure(0.3), ring: true).Routes[0];
        route.Loops.ShouldNotBeEmpty();
        var closureNodes = route.Loops.SelectMany(c => new[] { c.From, c.To }).ToHashSet();
        var points = FlushingPoints.Find([route], Norms);
        points.Count(p => closureNodes.Contains(p.NodeId)).ShouldBe(1);
    }

    [Fact]
    public void Coating_area_is_the_outer_surface_of_the_pipes()
    {
        // DN25 Ø33,5 × 4 м, DN50 Ø60 × 1 м, DN80 Ø88,5 × 2 м.
        var t = MaterialTakeoffBuilder.Build([Route], 3, Norms, "ГОСТ 3262", Sprinkler, coating: new CoatingSpec("Покрытие антикоррозионное", null));
        var item = t.Items.Single(i => i.Group == TakeoffGroup.Materials);
        item.Unit.ShouldBe("м²");
        item.Quantity.ShouldBe(Math.PI * ((0.0335 * 4) + (0.060 * 1) + (0.0885 * 2)), 0.05);
    }

    [Fact]
    public void Branded_sprinkler_carries_the_analog_note()
    {
        var t = MaterialTakeoffBuilder.Build([Route], 3, Norms, "ГОСТ 3262", Sprinkler with { Note = "или аналог" });
        t.Items.First(i => i.Group == TakeoffGroup.Sprinklers).Note.ShouldBe("или аналог");
    }
}
