using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

public class TreeSolverTests
{
    private static NetworkNode Sprinkler(string id, double z = 0, double k = 0.47) => new(id, NodeKind.Sprinkler, new Length(z), new KFactor(k));

    private static NetworkNode Tee(string id, double z = 0) => new(id, NodeKind.Tee, new Length(z));

    private static NetworkNode Source(string id, double z = 0) => new(id, NodeKind.Source, new Length(z));

    private static NetworkPipe Pipe(string id, string from, string to, double l = 3, double kt = 16.5) =>
        new(id, from, to, new Length(l), new PipeCharacteristic(kt));

    [Fact]
    public void Chain_of_two_sprinklers_matches_hand_calculation()
    {
        // Б.9–Б.10: P2 = P1 + q1²·L/(100·Kт), q2 = 10·K·√P2.
        var net = new HydraulicNetwork(
            [Source("S"), Sprinkler("1"), Sprinkler("2")],
            [Pipe("S-2", "S", "2"), Pipe("2-1", "2", "1")]);

        var result = TreeSolver.SolveDesign(net, "1", new Pressure(0.1));

        var q1 = 10 * 0.47 * Math.Sqrt(0.1);
        var p2 = 0.1 + (q1 * q1 * 3 / 1650);
        var q2 = 10 * 0.47 * Math.Sqrt(p2);
        result.Issues.ShouldBeEmpty();
        result.NodePressures["2"].Mpa.ShouldBe(p2, 1e-12);
        result.TotalFlow.LitersPerSecond.ShouldBe(q1 + q2, 1e-12);
        result.SourcePressure.Mpa.ShouldBe(p2 + ((q1 + q2) * (q1 + q2) * 3 / 1650), 1e-12);
        result.DictatingPath.ShouldBe(["2-1", "S-2"]);
    }

    [Fact]
    public void Side_branch_is_rescaled_to_the_junction_pressure()
    {
        // Две одинаковые ветви: вторая получает то же давление в узле — расходы равны (симметрия, Б.1.2.14).
        var net = new HydraulicNetwork(
            [Source("S"), Tee("a"), Sprinkler("L1"), Sprinkler("L2"), Sprinkler("R1"), Sprinkler("R2")],
            [Pipe("S-a", "S", "a", 5, 135), Pipe("a-L2", "a", "L2"), Pipe("L2-L1", "L2", "L1"), Pipe("a-R2", "a", "R2"), Pipe("R2-R1", "R2", "R1")]);

        var result = TreeSolver.SolveDesign(net, "L1", new Pressure(0.1));

        result.PipeFlows["a-R2"].LitersPerSecond.ShouldBe(result.PipeFlows["a-L2"].LitersPerSecond, 1e-12);
        result.NodePressures["R1"].Mpa.ShouldBe(0.1, 1e-12);
    }

    [Fact]
    public void Flows_balance_at_every_node()
    {
        var net = new HydraulicNetwork(
            [Source("S", 0), Tee("a", 3), Tee("b", 3), Sprinkler("1", 3.6), Sprinkler("2", 3.6), Sprinkler("3", 3.5), Sprinkler("4", 3.5)],
            [Pipe("S-b", "S", "b", 10, 1262), Pipe("b-a", "b", "a", 4, 517), Pipe("a-2", "a", "2"), Pipe("2-1", "2", "1"),
             Pipe("b-4", "b", "4", 2), Pipe("4-3", "4", "3")]);

        var result = TreeSolver.SolveDesign(net, "1", new Pressure(0.15));

        result.Issues.ShouldBeEmpty();
        var emitted = result.EmitterFlows.Values.Sum(f => f.LitersPerSecond);
        result.TotalFlow.LitersPerSecond.ShouldBe(emitted, 1e-12);
        result.PipeFlows["S-b"].LitersPerSecond.ShouldBe(emitted, 1e-12);
        result.PipeFlows["b-a"].LitersPerSecond.ShouldBe(result.EmitterFlows["1"].LitersPerSecond + result.EmitterFlows["2"].LitersPerSecond, 1e-12);
    }

    [Fact]
    public void Doubling_K_lowers_the_required_pressure()
    {
        HydraulicNetwork Net(double k) => new(
            [Source("S"), Sprinkler("1", 0, k), Sprinkler("2", 0, k)],
            [Pipe("S-2", "S", "2"), Pipe("2-1", "2", "1")]);

        // Тот же расход диктующего оросителя при вдвое большем K требует вчетверо меньшего давления.
        var q = 10 * 0.47 * Math.Sqrt(0.1);
        var p1 = Math.Pow(q / (10 * 0.94), 2);
        var small = TreeSolver.SolveDesign(Net(0.47), "1", new Pressure(0.1));
        var big = TreeSolver.SolveDesign(Net(0.94), "1", new Pressure(p1));

        big.SourcePressure.Mpa.ShouldBeLessThan(small.SourcePressure.Mpa);
    }

    [Fact]
    public void Ring_is_reported_as_an_issue_not_an_exception()
    {
        var net = new HydraulicNetwork(
            [Source("S"), Tee("a"), Tee("b"), Sprinkler("1")],
            [Pipe("S-a", "S", "a"), Pipe("S-b", "S", "b"), Pipe("a-1", "a", "1"), Pipe("b-1", "b", "1")]);

        var result = TreeSolver.SolveDesign(net, "1", new Pressure(0.1));

        result.HasErrors.ShouldBeTrue();
        result.Issues.ShouldContain(i => i.Code == "topology.notTree");
    }

    [Fact]
    public void Branch_with_emitters_at_different_elevations_is_rejected()
    {
        var net = new HydraulicNetwork(
            [Source("S"), Tee("a"), Sprinkler("1"), Tee("b"), Sprinkler("2", 0), Sprinkler("3", 1.0)],
            [Pipe("S-a", "S", "a"), Pipe("a-1", "a", "1"), Pipe("a-b", "a", "b"), Pipe("b-2", "b", "2"), Pipe("b-3", "b", "3")]);

        var result = TreeSolver.SolveDesign(net, "1", new Pressure(0.1));

        result.Issues.ShouldContain(i => i.Code == "branch.mixedElevation" && i.ElementId == "a-b");
    }

    [Fact]
    public void Missing_source_and_disconnected_nodes_are_issues()
    {
        var noSource = TreeSolver.SolveDesign(new HydraulicNetwork([Sprinkler("1")], []), "1", new Pressure(0.1));
        noSource.Issues.ShouldContain(i => i.Code == "topology.source");

        var loose = TreeSolver.SolveDesign(
            new HydraulicNetwork([Source("S"), Sprinkler("1"), Sprinkler("x")], [Pipe("S-1", "S", "1")]), "1", new Pressure(0.1));
        loose.Issues.ShouldContain(i => i.Code == "topology.disconnected" && i.ElementId == "x");
    }
}
