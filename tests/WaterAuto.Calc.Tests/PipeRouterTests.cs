using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class PipeRouterTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);
    private static readonly LayoutRules Rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
    private static readonly Pressure Low = new(0.3);

    private static Polygon Poly(params (double X, double Y)[] v) => new(v.Select(p => new PlanPoint(p.X, p.Y)).ToList());

    private static Polygon Rect(double x0, double y0, double x1, double y1) => Poly((x0, y0), (x1, y0), (x1, y1), (x0, y1));

    private static FloorPlan Plan(Polygon contour, PlanPoint inlet, IEnumerable<Column>? columns = null, IEnumerable<NoSprinklerZone>? zones = null) =>
        new([new Room("R1", "Автостоянка", contour)], [.. columns ?? []], [], [], [.. zones ?? []], [new Inlet("I1", inlet, "Стояк")]);

    private static (FloorPlan Plan, SprinklerLayout Layout, RoutingResult Routing) Route(FloorPlan plan, Pressure? pressure = null)
    {
        var layout = SprinklerLayouter.Layout(plan, Rules);
        return (plan, layout, PipeRouter.Route(plan, layout, Norms, Rules, pressure ?? Low));
    }

    [Theory]
    [InlineData(1, false, 25)] // DN 20 при давлении до 0,5 МПа в табл. Б.4 нет
    [InlineData(2, false, 25)]
    [InlineData(3, false, 32)]
    [InlineData(10, false, 50)]
    [InlineData(11, false, 70)]
    [InlineData(141, false, 150)] // «более 140»
    [InlineData(1, true, 20)]
    [InlineData(18, true, 50)]
    [InlineData(400, true, 150)]
    public void Initial_DN_comes_from_table_B_4(int fed, bool high, int dn)
    {
        var table = DnTable.Resolve(Norms, new Pressure(high ? 0.6 : 0.3))!;
        table.Pick(fed).ShouldBe(dn);
    }

    [Fact]
    public void Rectangle_is_routed_as_a_tree_from_the_inlet()
    {
        var (_, layout, routing) = Route(Plan(Rect(0, 0, 30000, 20000), new PlanPoint(500, 500)));
        routing.Issues.ShouldBeEmpty();
        var route = routing.Routes.ShouldHaveSingleItem();
        RouteChecks.ShouldBeTree(route);
        route.Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler).ShouldBe(layout.Sprinklers.Count);
        route.Segments.Single(s => s.From == "I1").Fed.ShouldBe(54);
        RouteChecks.ShouldBeAxisAligned(route, 0);

        // 6 рядов по 9 оросителей, питающий — между 4-м и 5-м или 5-м и 6-м: в ветви не больше 5.
        route.Segments.Count(s => s.Role == RouteSegmentRole.Main && route.Nodes.Single(n => n.Id == s.To).Kind == RouteNodeKind.Tee).ShouldBeGreaterThanOrEqualTo(5);
        route.Segments.Where(s => s.Role == RouteSegmentRole.Row).Max(s => s.Fed).ShouldBe(5);
    }

    [Fact]
    public void DN_does_not_grow_towards_the_ends()
    {
        var (_, _, routing) = Route(Plan(Rect(0, 0, 30000, 20000), new PlanPoint(500, 500)));
        var route = routing.Routes[0];
        var byTo = route.Segments.ToDictionary(s => s.To);
        foreach (var s in route.Segments.Where(s => byTo.ContainsKey(s.From)))
        {
            s.Dn.ShouldBeLessThanOrEqualTo(byTo[s.From].Dn, $"{s.Id} толще участка перед ним");
            s.Fed.ShouldBeLessThanOrEqualTo(byTo[s.From].Fed);
        }
    }

    [Fact]
    public void Rotated_room_is_routed_along_its_rows()
    {
        var angle = Math.PI / 6;
        PlanPoint R(double x, double y) => new((x * Math.Cos(angle)) - (y * Math.Sin(angle)), (x * Math.Sin(angle)) + (y * Math.Cos(angle)));
        var (_, layout, routing) = Route(Plan(new Polygon([R(0, 0), R(30000, 0), R(30000, 20000), R(0, 20000)]), R(500, 500)));
        routing.Issues.ShouldBeEmpty();
        var route = routing.Routes.ShouldHaveSingleItem();
        RouteChecks.ShouldBeTree(route);
        route.Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler).ShouldBe(layout.Sprinklers.Count);
        RouteChecks.ShouldBeAxisAligned(route, angle);
    }

    [Fact]
    public void L_shaped_room_is_reached_by_one_main()
    {
        var contour = Poly((0, 0), (30000, 0), (30000, 10000), (15000, 10000), (15000, 20000), (0, 20000));
        var (_, layout, routing) = Route(Plan(contour, new PlanPoint(500, 500)));
        routing.Issues.ShouldBeEmpty();
        var route = routing.Routes.ShouldHaveSingleItem();
        RouteChecks.ShouldBeTree(route);
        RouteChecks.ShouldStayInside(route, contour);
        route.Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler).ShouldBe(layout.Sprinklers.Count);
    }

    [Fact]
    public void Inlet_far_from_the_main_is_connected_by_a_bend()
    {
        // Ввод в углу на линии ряда: подводящий не должен лечь на трубу ряда.
        var (_, layout, routing) = Route(Plan(Rect(0, 0, 30000, 20000), new PlanPoint(29500, 1670)));
        routing.Issues.ShouldBeEmpty();
        var route = routing.Routes[0];
        RouteChecks.ShouldBeTree(route);
        RouteChecks.ShouldNotOverlap(route);
        route.Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler).ShouldBe(layout.Sprinklers.Count);
    }

    [Fact]
    public void Sprinklers_under_a_duct_are_routed_too()
    {
        var duct = new Duct("D1", Rect(2000, 15000, 12000, 16000), new Length(-0.9));
        var plan = Plan(Rect(0, 0, 30000, 20000), new PlanPoint(500, 500)) with { Ducts = [duct] };
        var layout = SprinklerLayouter.Layout(plan, Rules);
        var routing = PipeRouter.Route(plan, layout, Norms, Rules, Low);
        routing.Issues.Where(i => i.Level == IssueLevel.Error).ShouldBeEmpty(string.Join("; ", routing.Issues.Select(i => i.Message)));
        var route = routing.Routes.Single();
        RouteChecks.ShouldBeTree(route);
        route.Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler).ShouldBe(layout.Sprinklers.Count);
    }

    [Fact]
    public void Room_without_inlet_is_an_error()
    {
        var plan = Plan(Rect(0, 0, 30000, 20000), new PlanPoint(40000, 500));
        var (_, _, routing) = Route(plan);
        routing.Routes.ShouldBeEmpty();
        var issue = routing.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("routing.noInlet");
        issue.Level.ShouldBe(IssueLevel.Error);
        issue.ElementId.ShouldBe("R1");
    }

    [Fact]
    public void Each_room_needs_its_own_inlet()
    {
        // Два помещения, ввод только в одном: второе — явная ошибка, трассы между помещениями пока нет.
        var hall = new Room("R1", "Автостоянка", Rect(0, 0, 30000, 20000));
        var pumps = new Room("R2", "Насосная", Rect(30000, 0, 36000, 6000));
        var plan = new FloorPlan([hall, pumps], [], [], [], [], [new Inlet("I1", new PlanPoint(35000, 1000), "Узел управления")]);
        var (_, _, routing) = Route(plan);
        routing.Routes.ShouldHaveSingleItem().RoomId.ShouldBe("R2");
        var issue = routing.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("routing.noInlet");
        issue.ElementId.ShouldBe("R1");
        issue.Message.ShouldContain("ПТ_ВВОД");
    }

    [Fact]
    public void Row_through_a_column_is_a_warning()
    {
        // Раскладка сама уводит ряды от колонн, поэтому раскладка задана вручную: колонна между оросителями ряда.
        var column = new Column("C1", Rect(2300, 800, 2700, 1200));
        var plan = Plan(Rect(0, 0, 6000, 3000), new PlanPoint(500, 2500), [column]);
        var layout = new SprinklerLayout(
            [new PlacedSprinkler("R1.S1", "R1", new(1000, 1000)), new PlacedSprinkler("R1.S2", "R1", new(4000, 1000))],
            [new LayoutZone("R1", Rect(0, 0, 6000, 3000), 2, 0)],
            []);
        var routing = PipeRouter.Route(plan, layout, Norms, Rules, Low);
        routing.Routes.ShouldHaveSingleItem();
        var issue = routing.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("routing.throughColumn");
        issue.Level.ShouldBe(IssueLevel.Warning);
        issue.ElementId.ShouldBe("C1");
    }

    [Fact]
    public void Main_avoids_columns()
    {
        var columns = new List<Column>();
        for (var x = 7500; x < 30000; x += 7500)
        {
            for (var y = 5000; y < 20000; y += 5000)
            {
                columns.Add(new Column($"C{x}-{y}", Rect(x - 200, y - 200, x + 200, y + 200)));
            }
        }

        var (_, _, routing) = Route(Plan(Rect(0, 0, 30000, 20000), new PlanPoint(500, 500), columns));
        routing.Issues.Where(i => i.Level == IssueLevel.Error).ShouldBeEmpty();
        var route = routing.Routes[0];
        var nodes = route.Nodes.ToDictionary(n => n.Id);
        foreach (var s in route.Segments.Where(s => s.Role == RouteSegmentRole.Main))
        {
            var a = nodes[s.From].Location;
            var b = nodes[s.To].Location;
            columns.Any(c => RouteChecks.Crosses(c.Outline, a, b)).ShouldBeFalse($"питающий {s.Id} проходит через колонну");
        }
    }

    [Fact]
    public void Row_split_by_a_no_sprinkler_zone_stays_out_of_it()
    {
        var zone = new NoSprinklerZone("Z1", Rect(12000, 0, 18000, 20000));
        var plan = Plan(Rect(0, 0, 30000, 20000), new PlanPoint(500, 500), zones: [zone]);
        var (_, layout, routing) = Route(plan);
        var route = routing.Routes.ShouldHaveSingleItem();
        RouteChecks.ShouldBeTree(route);
        var nodes = route.Nodes.ToDictionary(n => n.Id);
        foreach (var s in route.Segments)
        {
            RouteChecks.Crosses(zone.Outline, nodes[s.From].Location, nodes[s.To].Location).ShouldBeFalse($"{s.Id} проходит через зону запрета");
        }

        // Зона делит помещение на две части без прохода: одна часть не достигнута — ошибка с координатами, а не молчание.
        route.Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler).ShouldBeLessThan(layout.Sprinklers.Count);
        routing.Issues.ShouldContain(i => i.Code == "routing.rowNotReached" && i.Level == IssueLevel.Error && i.Location != null);
    }

    [Fact]
    public void Rows_behind_a_zone_get_a_second_main_through_a_free_corridor()
    {
        // Зона перекрывает зал, кроме коридора внизу: ряды справа от зоны — второй питающий, подключённый через коридор.
        var zone = new NoSprinklerZone("Z1", Rect(18000, 4000, 22000, 20000));
        var plan = Plan(Rect(0, 0, 40000, 20000), new PlanPoint(500, 500), zones: [zone]);
        var (_, layout, routing) = Route(plan);
        var route = routing.Routes.ShouldHaveSingleItem();
        RouteChecks.ShouldBeTree(route);
        routing.Issues.ShouldNotContain(i => i.Code == "routing.rowNotReached");
        route.Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler).ShouldBe(layout.Sprinklers.Count);
        var nodes = route.Nodes.ToDictionary(n => n.Id);
        foreach (var s in route.Segments)
        {
            RouteChecks.Crosses(zone.Outline, nodes[s.From].Location, nodes[s.To].Location).ShouldBeFalse($"{s.Id} проходит через зону запрета");
        }
    }

    [Fact]
    public void Routed_network_solves_with_the_tree_solver()
    {
        var (_, _, routing) = Route(Plan(Rect(0, 0, 30000, 20000), new PlanPoint(500, 500)));
        var built = RouteNetwork.Build(routing.Routes[0], Norms, "ГОСТ 3262", new KFactor(0.47), new Length(-1.5));
        built.Issues.Where(i => i.Level != IssueLevel.Info).ShouldBeEmpty();
        var network = built.Network!;
        network.Nodes.Count(n => n.Kind == NodeKind.Sprinkler).ShouldBe(54);

        // Диктующий — самый удалённый от ввода по трассе.
        var dictating = RouteChecks.FarthestSprinkler(routing.Routes[0]);
        var solution = TreeSolver.SolveDesign(network, dictating, new Pressure(0.1));
        solution.Issues.Where(i => i.Level == IssueLevel.Error).ShouldBeEmpty();
        solution.EmitterFlows.Count.ShouldBe(54);
        solution.TotalFlow.LitersPerSecond.ShouldBe(solution.EmitterFlows.Values.Sum(f => f.LitersPerSecond), 1e-6);
        solution.EmitterFlows.Values.ShouldAllBe(f => f.LitersPerSecond >= TreeSolver.EmitterFlow(new KFactor(0.47), new Pressure(0.1)).LitersPerSecond - 1e-9);
    }

    [Fact]
    public void DN_missing_in_the_pipe_series_is_replaced_by_the_next_larger()
    {
        // В ГОСТ 3262 (табл. Б.2) нет DN 70 из табл. Б.4 — берётся DN 80, с замечанием.
        var route = new PipeRoute("R1", "I1",
            [new RouteNode("I1", RouteNodeKind.Inlet, new(0, 0)), new RouteNode("S1", RouteNodeKind.Sprinkler, new(1000, 0), "S1")],
            [new RouteSegment("P1", "I1", "S1", RouteSegmentRole.Supply, new Length(1), 1, 70)]);
        var built = RouteNetwork.Build(route, Norms, "ГОСТ 3262", new KFactor(0.47), Length.FromMillimeters(0));
        built.Network!.Pipes.ShouldHaveSingleItem().Characteristic.Kt.ShouldBe(1262);
        var issue = built.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("network.dnSubstituted");
        issue.Level.ShouldBe(IssueLevel.Info);
    }
}
