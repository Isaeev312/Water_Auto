using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

public sealed record RouteNetworkResult(HydraulicNetwork? Network, IReadOnlyList<Issue> Issues);

/// <summary>
/// Расчётная сеть из трассы: ввод — источник, оросители — эмиттеры с K, Kт — строка табл. Б.2 стандарта трубы по DN.
/// DN, которого нет в сортаменте (DN 70 из табл. Б.4), заменяется ближайшим большим — с замечанием (В-19).
/// </summary>
public static class RouteNetwork
{
    public static RouteNetworkResult Build(PipeRoute route, INormsProvider norms, string standard, KFactor sprinklerK, Length elevation, double lossMultiplier = 1.0)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        var series = norms.ByClause("табл. Б.2")
            .Where(r => r.Attributes.GetValueOrDefault("standard") == standard && r.Attributes.GetValueOrDefault("outdoorOnly") != "true")
            .OrderBy(r => r.Values["dn"])
            .ToList();
        var resolved = new Dictionary<int, PipeCharacteristic?>();
        PipeCharacteristic? Kt(int dn)
        {
            if (resolved.TryGetValue(dn, out var known))
            {
                return known;
            }

            var actual = series.Select(r => (int)r.Values["dn"]!.Value).Where(d => d >= dn).DefaultIfEmpty(-1).Min();
            if (actual < 0)
            {
                issues.Add(new Issue("network.noPipe", IssueLevel.Error, $"В табл. Б.2 для {standard} нет трубы DN {dn} и больше.", Clause: "табл. Б.2"));
                return resolved[dn] = null;
            }

            var rows = series.Where(r => (int)r.Values["dn"]!.Value == actual).ToList();
            if (actual != dn)
            {
                issues.Add(new Issue("network.dnSubstituted", IssueLevel.Info, $"DN {dn} (табл. Б.4) нет в сортаменте {standard} — принят DN {actual}.", rows[0].Id, "табл. Б.4"));
            }

            if (rows.Count > 1)
            {
                issues.Add(new Issue("network.pipeAmbiguous", IssueLevel.Warning, $"Для {standard} DN {actual} в табл. Б.2 несколько труб — принята {rows[0].Id}; задайте трубу явно.", rows[0].Id, "табл. Б.2"));
            }

            return resolved[dn] = new PipeCharacteristic(rows[0].Values["Kt"]!.Value);
        }

        var nodes = route.Nodes.Select(n => n.Kind switch
        {
            RouteNodeKind.Inlet => new NetworkNode(n.Id, NodeKind.Source, elevation),
            RouteNodeKind.Sprinkler => new NetworkNode(n.Id, NodeKind.Sprinkler, elevation, sprinklerK),
            _ => new NetworkNode(n.Id, NodeKind.Tee, elevation),
        }).ToList();
        var pipes = new List<NetworkPipe>();
        foreach (var s in route.Segments)
        {
            if (Kt(s.Dn) is { } kt)
            {
                pipes.Add(new NetworkPipe(s.Id, s.From, s.To, s.Length, kt, lossMultiplier));
            }
        }

        return issues.Any(i => i.Level == IssueLevel.Error)
            ? new RouteNetworkResult(null, issues)
            : new RouteNetworkResult(new HydraulicNetwork(nodes, pipes), issues);
    }
}
