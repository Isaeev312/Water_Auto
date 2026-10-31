using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

/// <summary>
/// Сеть из трассы должна нести сортамент каждой трубы: иначе скорости (п. 6.7.1.37) не считаются, а расчёт выдаёт
/// «труба без сортамента» на каждой трубе (на реальном проекте — 300 предупреждений).
/// </summary>
public class RouteNetworkGeometryTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);
    private static readonly LayoutRules Rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;

    [Fact]
    public void Network_pipes_carry_geometry_and_velocity_is_computed()
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var layout = SprinklerLayouter.Layout(plan, Rules);
        var route = PipeRouter.Route(plan, layout, Norms, Rules, new Pressure(0.3)).Routes[0];
        var built = RouteNetwork.Build(route, Norms, "ГОСТ 3262-75", new KFactor(0.47), Length.FromMillimeters(0), 1.2);
        built.Issues.ShouldNotContain(i => i.Code == "pipe.noGeometry");
        built.Network.ShouldNotBeNull();
        built.Network!.Pipes.ShouldAllBe(p => p.Geometry != null);
        var leaf = route.Nodes.First(n => n.Kind == RouteNodeKind.Sprinkler && route.Segments.All(s => s.From != n.Id)).Id;
        var solution = TreeSolver.SolveDesign(built.Network!, leaf, new Pressure(0.211));
        solution.PipeVelocities.Count.ShouldBe(built.Network!.Pipes.Count);
        solution.PipeVelocities.Values.ShouldAllBe(v => v.MetersPerSecond > 0);
    }
}
