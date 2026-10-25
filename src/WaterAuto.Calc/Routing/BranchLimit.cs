using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>Ветвь — рядок от тройника (отвода) питающего трубопровода до последнего оросителя.</summary>
public sealed record Branch(string FirstSegmentId, string FromNodeId, int Sprinklers);

/// <summary>
/// Предел числа оросителей на ветви, заданный пользователем (В-09). Действующий СП 485 такого предела не устанавливает,
/// поэтому значение не норма, а выбор проекта; превышение — предупреждение.
/// </summary>
public static class BranchLimit
{
    public static IReadOnlyList<Branch> Branches(PipeRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var nodes = route.Nodes.ToDictionary(n => n.Id);
        return route.Segments
            .Where(s => s.Role == RouteSegmentRole.Row && nodes[s.From].Kind != RouteNodeKind.Sprinkler)
            .Select(s => new Branch(s.Id, s.From, s.Fed))
            .ToList();
    }

    public static IReadOnlyList<Issue> Check(PipeRoute route, int maxSprinklers)
    {
        var nodes = route.Nodes.ToDictionary(n => n.Id);
        return Branches(route)
            .Where(b => b.Sprinklers > maxSprinklers)
            .Select(b => new Issue(
                "routing.branchLimit",
                IssueLevel.Warning,
                $"Ветвь от участка {b.FirstSegmentId}: {b.Sprinklers} оросит. больше предела проекта {maxSprinklers}.",
                b.FirstSegmentId,
                Location: nodes[b.FromNodeId].Location))
            .ToList();
    }
}
