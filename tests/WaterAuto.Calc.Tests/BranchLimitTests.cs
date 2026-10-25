using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class BranchLimitTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);
    private static readonly LayoutRules Rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;

    private static PipeRoute Route()
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        return PipeRouter.Route(plan, SprinklerLayouter.Layout(plan, Rules), Norms, Rules, new Pressure(0.3)).Routes[0];
    }

    [Fact]
    public void Branch_counts_cover_every_sprinkler()
    {
        var route = Route();
        BranchLimit.Branches(route).Sum(b => b.Sprinklers).ShouldBe(route.Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler));
    }

    [Fact]
    public void Branches_over_the_user_limit_get_a_warning()
    {
        var route = Route();
        var max = BranchLimit.Branches(route).Max(b => b.Sprinklers);
        BranchLimit.Check(route, max).ShouldBeEmpty();
        var issues = BranchLimit.Check(route, max - 1);
        issues.ShouldNotBeEmpty();
        issues.ShouldAllBe(i => i.Level == IssueLevel.Warning && i.Code == "routing.branchLimit");
    }
}
