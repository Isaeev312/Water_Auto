using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class LoopSolverTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);
    private static readonly KFactor K = new(0.47);
    private static readonly Length Zero = Length.FromMillimeters(0);

    private static NetworkPipe Pipe(string id, string from, string to, double length, double kt = 110) =>
        new(id, from, to, new Length(length), new PipeCharacteristic(kt), 1.2);

    [Fact]
    public void Tree_network_gives_the_tree_solver_result()
    {
        // Ворота: на тупиковой сети узловой решатель совпадает с последовательным расчётом Б.9–Б.23.
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
        var route = PipeRouter.Route(plan, SprinklerLayouter.Layout(plan, rules), Norms, rules, new Pressure(0.3)).Routes[0];
        var network = RouteNetwork.Build(route, Norms, "ГОСТ 3262", K, Zero, 1.2).Network!;
        var dictating = route.Nodes.Where(n => n.Kind == RouteNodeKind.Sprinkler).Last().Id;

        var tree = TreeSolver.SolveDesign(network, dictating, new Pressure(0.2));
        var loop = LoopSolver.SolveDesign(network, dictating, new Pressure(0.2));

        loop.Issues.ShouldNotContain(i => i.Level == IssueLevel.Error);
        loop.SourcePressure.Mpa.ShouldBe(tree.SourcePressure.Mpa, 1e-6);
        loop.TotalFlow.LitersPerSecond.ShouldBe(tree.TotalFlow.LitersPerSecond, 1e-6);
        loop.NodePressures[dictating].Mpa.ShouldBe(0.2, 1e-9);
        foreach (var (id, q) in tree.EmitterFlows)
        {
            loop.EmitterFlows[id].LitersPerSecond.ShouldBe(q.LitersPerSecond, 1e-6);
        }
    }

    [Fact]
    public void Symmetric_ring_fed_on_its_axis_splits_half_and_half()
    {
        // Источник S → A; кольцо A–B–D–C–A одинаковыми трубами; ороситель в D, напротив A.
        var network = new HydraulicNetwork(
            [
                new NetworkNode("S", NodeKind.Source, Zero),
                new NetworkNode("A", NodeKind.Tee, Zero),
                new NetworkNode("B", NodeKind.Tee, Zero),
                new NetworkNode("C", NodeKind.Tee, Zero),
                new NetworkNode("D", NodeKind.Sprinkler, Zero, K),
            ],
            [Pipe("P0", "S", "A", 5), Pipe("P1", "A", "B", 10), Pipe("P2", "B", "D", 10), Pipe("P3", "A", "C", 10), Pipe("P4", "C", "D", 10)]);
        var r = LoopSolver.SolveDesign(network, "D", new Pressure(0.2));
        var q = r.EmitterFlows["D"].LitersPerSecond;
        r.PipeFlows["P1"].LitersPerSecond.ShouldBe(q / 2, 1e-9);
        r.PipeFlows["P3"].LitersPerSecond.ShouldBe(q / 2, 1e-9);
        r.TotalFlow.LitersPerSecond.ShouldBe(q, 1e-9);
    }

    [Fact]
    public void Unequal_ring_balances_mass_and_head_around_the_loop()
    {
        var network = new HydraulicNetwork(
            [
                new NetworkNode("S", NodeKind.Source, Zero),
                new NetworkNode("A", NodeKind.Tee, Zero),
                new NetworkNode("B", NodeKind.Sprinkler, Zero, K),
                new NetworkNode("C", NodeKind.Sprinkler, Zero, K),
                new NetworkNode("D", NodeKind.Sprinkler, Zero, K),
            ],
            [Pipe("P0", "S", "A", 5), Pipe("P1", "A", "B", 4), Pipe("P2", "B", "D", 15, 60), Pipe("P3", "A", "C", 12), Pipe("P4", "C", "D", 6)]);
        var r = LoopSolver.SolveDesign(network, "D", new Pressure(0.15));
        double Q(string id) => r.PipeFlows[id].LitersPerSecond;
        double L(string id) => r.PipeLosses[id].Mpa;

        // Узлы: приток = отток + эмиттер.
        Q("P0").ShouldBe(Q("P1") + Q("P3"), 1e-9);
        (Q("P1") - Q("P2")).ShouldBe(r.EmitterFlows["B"].LitersPerSecond, 1e-9);
        (Q("P3") - Q("P4")).ShouldBe(r.EmitterFlows["C"].LitersPerSecond, 1e-9);
        (Q("P2") + Q("P4")).ShouldBe(r.EmitterFlows["D"].LitersPerSecond, 1e-9);

        // Кольцо: потери по обе стороны от A до D равны.
        (L("P1") + L("P2")).ShouldBe(L("P3") + L("P4"), 1e-9);
    }
}
