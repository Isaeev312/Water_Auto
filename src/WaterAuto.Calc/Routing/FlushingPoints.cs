using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>Место промывочного устройства: узел трассы, DN трубы в этом месте и DN устройства.</summary>
public sealed record FlushingPoint(string RoomId, string NodeId, PlanPoint Location, int PipeDn, int Dn);

/// <summary>
/// Промывочные заглушки (краны) по п. 6.7.1.15: в конце каждого тупикового питающего трубопровода и в наиболее удалённой
/// от ввода точке кольца. DN устройства — не менее нормы, при меньшей трубе — DN трубы.
/// Распределительные рядки и подводящий трубопровод не учитываются (вопрос В-24).
/// </summary>
public static class FlushingPoints
{
    public static IReadOnlyList<FlushingPoint> Find(IReadOnlyList<PipeRoute> routes, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(norms);
        var minDn = (int)(norms.Find("sp485.p6_7_1_15.flushing")?.Values["minDn"]
            ?? throw new InvalidOperationException("В данных норм нет sp485.p6_7_1_15.flushing (minDn)."));
        var points = new List<FlushingPoint>();
        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            var incoming = route.Segments.ToDictionary(s => s.To);
            var closureNodes = route.Loops.SelectMany(c => new[] { c.From, c.To }).ToHashSet();
            var mainChildren = route.Segments.Where(s => s.Role == RouteSegmentRole.Main).Select(s => s.From).ToHashSet();
            FlushingPoint At(string node, int dn) => new(route.RoomId, node, nodes[node].Location, dn, Math.Min(dn, minDn));

            // Тупики: питающий участок, за которым нет питающего продолжения и который не замкнут в кольцо.
            foreach (var end in route.Segments.Where(s => s.Role == RouteSegmentRole.Main && !mainChildren.Contains(s.To) && !closureNodes.Contains(s.To)))
            {
                points.Add(At(end.To, end.Dn));
            }

            // Кольцо: конец замыкающего участка, дальний от ввода по трассе.
            if (route.Loops.Count > 0)
            {
                double Distance(string node)
                {
                    var d = 0.0;
                    while (incoming.TryGetValue(node, out var s))
                    {
                        d += s.Length.Meters;
                        node = s.From;
                    }

                    return d;
                }

                var far = route.Loops.SelectMany(c => new[] { (Node: c.From, c.Dn), (Node: c.To, c.Dn) }).MaxBy(x => Distance(x.Node));
                points.Add(At(far.Node, far.Dn));
            }
        }

        return points;
    }
}
