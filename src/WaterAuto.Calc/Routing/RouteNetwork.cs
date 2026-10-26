using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

public sealed record RouteNetworkResult(HydraulicNetwork? Network, IReadOnlyList<Issue> Issues);

/// <summary>
/// Расчётная сеть из трассы: ввод — источник, оросители — эмиттеры с K, Kт — строка табл. Б.2 стандарта трубы по DN.
/// DN, которого нет в сортаменте (DN 70 из табл. Б.4), заменяется ближайшим большим — с замечанием (В-19).
/// </summary>
public static class RouteNetwork
{
    public static RouteNetworkResult Build(PipeRoute route, INormsProvider norms, string standard, KFactor sprinklerK, Length elevation, double lossMultiplier = 1.0, IReadOnlyList<PipeSize>? sizes = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(norms);
        var series = new PipeSeries(norms, standard, sizes);

        var nodes = route.Nodes.Select(n => n.Kind switch
        {
            RouteNodeKind.Inlet => new NetworkNode(n.Id, NodeKind.Source, elevation),
            RouteNodeKind.Sprinkler => new NetworkNode(n.Id, NodeKind.Sprinkler, elevation, sprinklerK),
            _ => new NetworkNode(n.Id, NodeKind.Tee, elevation),
        }).ToList();
        var pipes = new List<NetworkPipe>();
        foreach (var s in route.Segments.Concat(route.Loops))
        {
            if (series.Pick(s.Dn) is { } pipe)
            {
                // Сортамент известен — передаём геометрию: без неё не считаются скорости (6.7.1.37) и «труба без сортамента»
                // в замечаниях появляется у каждой трубы стальной сети (было: 300 предупреждений на реальном проекте).
                var geometry = new PipeGeometry(pipe.Standard, pipe.Dn, new Length(pipe.OuterDiameterMm / 1000), new Length(pipe.WallMm / 1000));
                pipes.Add(new NetworkPipe(s.Id, s.From, s.To, s.Length, pipe.Kt, lossMultiplier, geometry));
            }
        }

        var issues = series.Issues.ToList();
        return issues.Any(i => i.Level == IssueLevel.Error)
            ? new RouteNetworkResult(null, issues)
            : new RouteNetworkResult(new HydraulicNetwork(nodes, pipes), issues);
    }
}
