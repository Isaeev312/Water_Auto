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
    public void Duplicate_pipe_ids_are_rejected()
    {
        var net = new HydraulicNetwork(
            [Source("S"), Sprinkler("1"), Sprinkler("2")],
            [Pipe("duplicate", "S", "1"), Pipe("duplicate", "S", "2")]);

        var result = TreeSolver.SolveDesign(net, "1", new Pressure(0.1));

        result.HasErrors.ShouldBeTrue();
        result.Issues.ShouldContain(i => i.Code == "topology.duplicatePipe" && i.ElementId == "duplicate");
    }

    [Fact]
    public void Non_finite_or_non_positive_pipe_parameters_are_rejected()
    {
        var invalidPipes = new[]
        {
            Pipe("length-zero", "S", "1", 0),
            new NetworkPipe("length-nan", "S", "1", new Length(double.NaN), new PipeCharacteristic(16.5)),
            new NetworkPipe("length-infinite", "S", "1", new Length(double.PositiveInfinity), new PipeCharacteristic(16.5)),
            Pipe("kt-zero", "S", "1", 3, 0),
            new NetworkPipe("kt-nan", "S", "1", new Length(3), new PipeCharacteristic(double.NaN)),
            new NetworkPipe("kt-infinite", "S", "1", new Length(3), new PipeCharacteristic(double.PositiveInfinity)),
            new NetworkPipe("multiplier-zero", "S", "1", new Length(3), new PipeCharacteristic(16.5), 0),
            new NetworkPipe("multiplier-nan", "S", "1", new Length(3), new PipeCharacteristic(16.5), double.NaN),
            new NetworkPipe("multiplier-infinite", "S", "1", new Length(3), new PipeCharacteristic(16.5), double.PositiveInfinity),
        };

        foreach (var pipe in invalidPipes)
        {
            var result = TreeSolver.SolveDesign(
                new HydraulicNetwork([Source("S"), Sprinkler("1")], [pipe]), "1", new Pressure(0.1));

            result.HasErrors.ShouldBeTrue(pipe.Id);
        }
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

    [Fact]
    public void Dictating_node_must_have_the_lowest_emitter_pressure()
    {
        // Диктующий — ороситель с наименьшим давлением (Б.1.2.1). Узел «2» выбран диктующим,
        // но за ним стоит «1» с меньшим давлением: расчёт идёт, величины заполнены, но результат — ошибка.
        var net = new HydraulicNetwork(
            [Source("S"), Sprinkler("1"), Sprinkler("2"), Sprinkler("3")],
            [Pipe("S-3", "S", "3"), Pipe("3-2", "3", "2"), Pipe("2-1", "2", "1")]);

        var result = TreeSolver.SolveDesign(net, "2", new Pressure(0.1));

        result.HasErrors.ShouldBeTrue();
        result.Issues.ShouldContain(i => i.Code == "dictating.notMinimum" && i.ElementId == "1");
        result.NodePressures["1"].Mpa.ShouldBeLessThan(0.1);
        result.SourcePressure.Mpa.ShouldBeGreaterThan(0.1);
    }

    [Fact]
    public void Terminal_dictating_node_passes_the_minimum_pressure_check()
    {
        var net = new HydraulicNetwork(
            [Source("S"), Sprinkler("1"), Sprinkler("2"), Sprinkler("3")],
            [Pipe("S-3", "S", "3"), Pipe("3-2", "3", "2"), Pipe("2-1", "2", "1")]);

        TreeSolver.SolveDesign(net, "1", new Pressure(0.1)).Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Side_branch_without_pressure_is_an_error_not_a_silent_zero()
    {
        // Ороситель «2» на 30 м выше: воды он не получит. Раньше — только предупреждение и q = 0.
        var net = new HydraulicNetwork(
            [Source("S"), Tee("a"), Sprinkler("1", 0), Sprinkler("2", 30)],
            [Pipe("S-a", "S", "a"), Pipe("a-1", "a", "1"), Pipe("a-2", "a", "2")]);

        var result = TreeSolver.SolveDesign(net, "1", new Pressure(0.1));

        result.HasErrors.ShouldBeTrue();
        result.Issues.ShouldContain(i => i.Code == "branch.noPressure");
        result.Issues.ShouldContain(i => i.Code == "dictating.notMinimum" && i.ElementId == "2");
    }

    [Fact]
    public void Non_finite_or_non_positive_emitter_K_is_rejected()
    {
        foreach (var k in new[] { double.NaN, double.PositiveInfinity, 0, -0.47 })
        {
            var net = new HydraulicNetwork(
                [Source("S"), Sprinkler("1", 0, k), Sprinkler("2")],
                [Pipe("S-2", "S", "2"), Pipe("2-1", "2", "1")]);

            var result = TreeSolver.SolveDesign(net, "1", new Pressure(0.1));

            result.HasErrors.ShouldBeTrue(k.ToString(System.Globalization.CultureInfo.InvariantCulture));
            result.Issues.ShouldContain(i => i.Code == "node.emitter" && i.ElementId == "1");
            double.IsFinite(result.SourcePressure.Mpa).ShouldBeTrue();
        }
    }

    [Fact]
    public void Non_finite_elevation_is_rejected()
    {
        var net = new HydraulicNetwork(
            [Source("S"), Tee("a", double.NaN), Sprinkler("1"), Sprinkler("2")],
            [Pipe("S-a", "S", "a"), Pipe("a-1", "a", "1"), Pipe("a-2", "a", "2")]);

        var result = TreeSolver.SolveDesign(net, "1", new Pressure(0.1));

        result.HasErrors.ShouldBeTrue();
        result.Issues.ShouldContain(i => i.Code == "node.elevation" && i.ElementId == "a");
    }

    [Fact]
    public void Non_finite_or_non_positive_dictating_pressure_is_rejected()
    {
        var net = new HydraulicNetwork(
            [Source("S"), Sprinkler("1"), Sprinkler("2")],
            [Pipe("S-2", "S", "2"), Pipe("2-1", "2", "1")]);

        foreach (var p in new[] { double.NaN, double.PositiveInfinity, 0, -0.1 })
        {
            var result = TreeSolver.SolveDesign(net, "1", new Pressure(p));

            result.HasErrors.ShouldBeTrue(p.ToString(System.Globalization.CultureInfo.InvariantCulture));
            result.Issues.ShouldContain(i => i.Code == "dictating.pressure");
        }
    }

    [Fact]
    public void Node_kind_and_emitter_must_agree()
    {
        // Ороситель без K раньше молча считался тройником и выпадал из расхода установки.
        var noK = TreeSolver.SolveDesign(
            new HydraulicNetwork([Source("S"), Sprinkler("1"), new NetworkNode("2", NodeKind.Sprinkler, new Length(0))], [Pipe("S-2", "S", "2"), Pipe("2-1", "2", "1")]),
            "1", new Pressure(0.1));
        noK.Issues.ShouldContain(i => i.Code == "node.emitterMissing" && i.ElementId == "2");

        var hydrantNoK = TreeSolver.SolveDesign(
            new HydraulicNetwork([Source("S"), Sprinkler("1"), new NetworkNode("pk", NodeKind.Hydrant, new Length(0))], [Pipe("S-pk", "S", "pk"), Pipe("pk-1", "pk", "1")]),
            "1", new Pressure(0.1));
        hydrantNoK.Issues.ShouldContain(i => i.Code == "node.emitterMissing" && i.ElementId == "pk");

        var teeWithK = TreeSolver.SolveDesign(
            new HydraulicNetwork([Source("S"), Sprinkler("1"), new NetworkNode("a", NodeKind.Tee, new Length(0), new KFactor(0.47))], [Pipe("S-a", "S", "a"), Pipe("a-1", "a", "1")]),
            "1", new Pressure(0.1));
        teeWithK.Issues.ShouldContain(i => i.Code == "node.emitterUnexpected" && i.ElementId == "a");

        var sourceWithK = TreeSolver.SolveDesign(
            new HydraulicNetwork([new NetworkNode("S", NodeKind.Source, new Length(0), new KFactor(0.47)), Sprinkler("1")], [Pipe("S-1", "S", "1")]),
            "1", new Pressure(0.1));
        sourceWithK.Issues.ShouldContain(i => i.Code == "node.emitterUnexpected" && i.ElementId == "S");
    }

    [Fact]
    public void Velocity_is_reported_for_pipes_with_geometry()
    {
        // ВГП 42,3×2,8 (DN32): внутренний Ø 36,7 мм. v = Q / (π·d²/4), Q в м³/с.
        var geometry = new PipeGeometry("ГОСТ 3262", 32, Length.FromMillimeters(42.3), Length.FromMillimeters(2.8));
        var net = new HydraulicNetwork(
            [Source("S"), Sprinkler("1"), Sprinkler("2")],
            [new NetworkPipe("S-2", "S", "2", new Length(3), new PipeCharacteristic(16.5), 1.0, geometry), Pipe("2-1", "2", "1")]);

        var result = TreeSolver.SolveDesign(net, "1", new Pressure(0.1));

        result.Issues.ShouldBeEmpty();
        var q = result.PipeFlows["S-2"].LitersPerSecond / 1000.0;
        var expected = q / (Math.PI * 0.0367 * 0.0367 / 4);
        result.PipeVelocities["S-2"].MetersPerSecond.ShouldBe(expected, 1e-9);
        result.PipeVelocities.ContainsKey("2-1").ShouldBeFalse();
        geometry.InnerDiameter.Millimeters.ShouldBe(36.7, 1e-9);
    }

    [Fact]
    public void Pipe_geometry_without_positive_inner_diameter_is_rejected()
    {
        foreach (var (od, wall) in new[] { (42.3, 22.0), (42.3, double.NaN), (0.0, 2.8), (-42.3, 2.8) })
        {
            var geometry = new PipeGeometry("ГОСТ 3262", 32, Length.FromMillimeters(od), Length.FromMillimeters(wall));
            var result = TreeSolver.SolveDesign(
                new HydraulicNetwork([Source("S"), Sprinkler("1")], [new NetworkPipe("S-1", "S", "1", new Length(3), new PipeCharacteristic(16.5), 1.0, geometry)]),
                "1", new Pressure(0.1));

            result.Issues.ShouldContain(i => i.Code == "pipe.geometry" && i.ElementId == "S-1", $"{od}x{wall}");
        }
    }
}
