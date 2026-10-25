using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

public sealed record PipeSupportsResult(IReadOnlyList<SupportPoint> Points, IReadOnlyDictionary<int, double> SpacingByDn, IReadOnlyList<string> NormIds, IReadOnlyList<Issue> Issues)
{
    /// <summary>Наибольшее расстояние между опорами для DN, м (табл. 6.7.1).</summary>
    public double MaxSpacing(int dn) => SpacingByDn[dn];
}

/// <summary>
/// Опоры стальных трубопроводов по трассе (СП 485): прямые участки одного DN делятся на равные пролёты не больше
/// табл. 6.7.1 по наружному диаметру — для диаметра, которого нет в таблице, берётся ближайший меньший (строже, В-21);
/// крайние опоры — на расстоянии соединения от узлов (6.7.1.31), у последнего оросителя — не дальше 6.7.1.26.
/// Короткий участок, для которого достаточно одной опоры посередине, получает одну.
/// </summary>
public static class PipeSupports
{
    public static PipeSupportsResult Build(IReadOnlyList<PipeRoute> routes, INormsProvider norms, string pipeStandard, IReadOnlyList<PipeSize>? sizes = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(norms);
        var series = new PipeSeries(norms, pipeStandard, sizes);
        var table = norms.ByClause("табл. 6.7.1").OrderBy(r => r.Values["outerDiameter"]!.Value).ToList();
        var last = norms.Find("sp485.p6_7_1_26.lastSprinkler")!;
        var joints = norms.Find("sp485.p6_7_1_31.joints")!;
        var offset = joints.Values["jointFromSupport"]!.Value / 1000;
        var issues = new List<Issue>();
        var points = new List<SupportPoint>();
        var spacing = new Dictionary<int, double>();
        var used = new HashSet<string> { last.Id, joints.Id };

        double Spacing(int dn)
        {
            if (spacing.TryGetValue(dn, out var s))
            {
                return s;
            }

            var od = series.Pick(dn)?.OuterDiameterMm ?? 0;
            var row = table.LastOrDefault(r => r.Values["outerDiameter"]!.Value <= od + 1e-9) ?? table[0];
            used.Add(row.Id);
            return spacing[dn] = row.Values["maxSpacing"]!.Value;
        }

        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            foreach (var chain in Chains(route, nodes))
            {
                var dn = chain[0].Dn;
                var s = Spacing(dn);
                var a = nodes[chain[0].From].Location;
                var b = nodes[chain[^1].To].Location;
                var length = chain.Sum(x => x.Length.Meters);
                var endsAtSprinkler = nodes[chain[^1].To].Kind == RouteNodeKind.Sprinkler && !route.Segments.Any(x => x.From == chain[^1].To);
                var limit = dn <= last.Values["smallDnMax"]!.Value ? last.Values["smallDistance"]!.Value : last.Values["largeDistance"]!.Value;

                // Положения опор вдоль прямой, м от начала.
                var at = new List<double>();
                if (length <= s && (!endsAtSprinkler || length / 2 <= limit))
                {
                    at.Add(length / 2);
                }
                else
                {
                    var span = Math.Max(length - (2 * offset), 0);
                    var n = (int)Math.Ceiling((span / s) - 1e-9);
                    for (var k = 0; k <= n; k++)
                    {
                        at.Add(offset + (n == 0 ? 0 : span * k / n));
                    }
                }

                if (endsAtSprinkler && length - at.Max() > limit + 1e-9)
                {
                    issues.Add(new Issue("supports.lastSprinkler", IssueLevel.Warning, $"Участок {chain[^1].Id} DN{dn}: опора дальше {limit} м от последнего оросителя.", chain[^1].Id, "6.7.1.26"));
                }

                foreach (var t in at)
                {
                    var f = length > 0 ? t / length : 0;
                    var p = new PlanPoint(a.XMm + ((b.XMm - a.XMm) * f), a.YMm + ((b.YMm - a.YMm) * f));
                    var seg = SegmentAt(chain, t);
                    points.Add(new SupportPoint(p, dn, seg.Id));
                }
            }
        }

        return new PipeSupportsResult(points, spacing, [.. used], [.. issues, .. series.Issues]);
    }

    private static RouteSegment SegmentAt(List<RouteSegment> chain, double t)
    {
        var acc = 0.0;
        foreach (var s in chain)
        {
            acc += s.Length.Meters;
            if (t <= acc + 1e-9)
            {
                return s;
            }
        }

        return chain[^1];
    }

    // Прямые цепочки одного DN: участок продолжается следующим, если тот выходит из его конца по той же прямой.
    private static IEnumerable<List<RouteSegment>> Chains(PipeRoute route, Dictionary<string, RouteNode> nodes)
    {
        var outgoing = route.Segments.ToLookup(s => s.From);
        RouteSegment? Next(RouteSegment s) => outgoing[s.To].FirstOrDefault(o => o.Dn == s.Dn && Collinear(nodes, s, o));
        var continued = route.Segments.Select(Next).Where(x => x is not null).Select(x => x!.Id).ToHashSet();
        foreach (var start in route.Segments.Where(s => !continued.Contains(s.Id)))
        {
            var chain = new List<RouteSegment> { start };
            for (var n = Next(start); n is not null; n = Next(n))
            {
                chain.Add(n);
            }

            yield return chain;
        }

        foreach (var c in route.Loops)
        {
            yield return [c];
        }
    }

    private static bool Collinear(Dictionary<string, RouteNode> nodes, RouteSegment a, RouteSegment b)
    {
        var (p, q, r) = (nodes[a.From].Location, nodes[a.To].Location, nodes[b.To].Location);
        double ux = q.XMm - p.XMm, uy = q.YMm - p.YMm, vx = r.XMm - q.XMm, vy = r.YMm - q.YMm;
        var cross = (ux * vy) - (uy * vx);
        return Math.Abs(cross) < 1e-6 * Math.Sqrt(((ux * ux) + (uy * uy)) * ((vx * vx) + (vy * vy))) && ((ux * vx) + (uy * vy)) > 0;
    }
}
