using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

/// <summary>Проверки трассы без знания алгоритма: дерево от ввода, участки вдоль осей, внутри помещения, без наложений.</summary>
internal static class RouteChecks
{
    public static void ShouldBeTree(PipeRoute route)
    {
        var nodes = route.Nodes.ToDictionary(n => n.Id);
        route.Segments.ShouldAllBe(s => nodes.ContainsKey(s.From) && nodes.ContainsKey(s.To));
        foreach (var n in route.Nodes.Where(n => n.Id != route.InletId))
        {
            route.Segments.Count(s => s.To == n.Id).ShouldBe(1, $"в узел {n.Id} должен входить ровно один участок");
        }

        route.Segments.ShouldNotContain(s => s.To == route.InletId);
        Reach(route).Count.ShouldBe(route.Nodes.Count, "не все узлы связаны с вводом");

        // Fed — число оросителей за участком.
        foreach (var s in route.Segments)
        {
            Subtree(route, s.To).Count(id => nodes[id].Kind == RouteNodeKind.Sprinkler).ShouldBe(s.Fed, s.Id);
            s.Length.Meters.ShouldBeGreaterThan(0, s.Id);
        }
    }

    public static void ShouldBeAxisAligned(PipeRoute route, double angle)
    {
        var nodes = route.Nodes.ToDictionary(n => n.Id);
        foreach (var s in route.Segments)
        {
            var (a, b) = (nodes[s.From].Location, nodes[s.To].Location);
            double dx = b.XMm - a.XMm, dy = b.YMm - a.YMm;
            var along = Math.Abs((dx * Math.Cos(angle)) + (dy * Math.Sin(angle)));
            var across = Math.Abs((-dx * Math.Sin(angle)) + (dy * Math.Cos(angle)));
            Math.Min(along, across).ShouldBeLessThan(1, $"{s.Id} не вдоль осей помещения");
        }
    }

    public static void ShouldStayInside(PipeRoute route, Polygon contour)
    {
        var nodes = route.Nodes.ToDictionary(n => n.Id);
        foreach (var s in route.Segments)
        {
            var (a, b) = (nodes[s.From].Location, nodes[s.To].Location);
            for (var t = 0.0; t <= 1; t += 0.05)
            {
                contour.Contains(new PlanPoint(a.XMm + (t * (b.XMm - a.XMm)), a.YMm + (t * (b.YMm - a.YMm)))).ShouldBeTrue($"{s.Id} выходит из помещения");
            }
        }
    }

    public static void ShouldNotOverlap(PipeRoute route)
    {
        var nodes = route.Nodes.ToDictionary(n => n.Id);
        var segs = route.Segments.Select(s => (s.Id, A: nodes[s.From].Location, B: nodes[s.To].Location)).ToList();
        for (var i = 0; i < segs.Count; i++)
        {
            for (var j = i + 1; j < segs.Count; j++)
            {
                Overlap(segs[i].A, segs[i].B, segs[j].A, segs[j].B).ShouldBeLessThan(1, $"{segs[i].Id} и {segs[j].Id} лежат друг на друге");
            }
        }
    }

    /// <summary>Проходит ли отрезок через внутренность многоугольника (касание границы не считается).</summary>
    public static bool Crosses(Polygon polygon, PlanPoint a, PlanPoint b)
    {
        var n = (int)Math.Ceiling(LayoutChecks.Distance(a, b) / 10) + 1;
        for (var k = 0; k <= n; k++)
        {
            var p = new PlanPoint(a.XMm + (k * (b.XMm - a.XMm) / n), a.YMm + (k * (b.YMm - a.YMm) / n));
            if (polygon.Contains(p) && polygon.Edges().All(e => DistanceToSegment(p, e.A, e.B) > 1))
            {
                return true;
            }
        }

        return false;
    }

    public static string FarthestSprinkler(PipeRoute route)
    {
        var dist = new Dictionary<string, double> { [route.InletId] = 0 };
        var queue = new Queue<string>([route.InletId]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var s in route.Segments.Where(s => s.From == id))
            {
                dist[s.To] = dist[id] + s.Length.Meters;
                queue.Enqueue(s.To);
            }
        }

        return route.Nodes.Where(n => n.Kind == RouteNodeKind.Sprinkler).MaxBy(n => dist[n.Id])!.Id;
    }

    private static HashSet<string> Reach(PipeRoute route) => Subtree(route, route.InletId);

    private static HashSet<string> Subtree(PipeRoute route, string root)
    {
        var seen = new HashSet<string> { root };
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            foreach (var s in route.Segments.Where(s => s.From == id))
            {
                if (seen.Add(s.To))
                {
                    stack.Push(s.To);
                }
            }
        }

        return seen;
    }

    // Длина общей части двух коллинеарных отрезков (0, если не коллинеарны).
    private static double Overlap(PlanPoint a, PlanPoint b, PlanPoint c, PlanPoint d)
    {
        double ux = b.XMm - a.XMm, uy = b.YMm - a.YMm;
        var len = Math.Sqrt((ux * ux) + (uy * uy));
        if (len < 1e-9)
        {
            return 0;
        }

        (ux, uy) = (ux / len, uy / len);
        double Cross(PlanPoint p) => (ux * (p.YMm - a.YMm)) - (uy * (p.XMm - a.XMm));
        if (Math.Abs(Cross(c)) > 1 || Math.Abs(Cross(d)) > 1)
        {
            return 0;
        }

        double T(PlanPoint p) => (ux * (p.XMm - a.XMm)) + (uy * (p.YMm - a.YMm));
        var lo = Math.Max(0, Math.Min(T(c), T(d)));
        var hi = Math.Min(len, Math.Max(T(c), T(d)));
        return Math.Max(0, hi - lo);
    }

    private static double DistanceToSegment(PlanPoint p, PlanPoint a, PlanPoint b)
    {
        double dx = b.XMm - a.XMm, dy = b.YMm - a.YMm;
        var len2 = (dx * dx) + (dy * dy);
        var t = len2 == 0 ? 0 : Math.Clamp((((p.XMm - a.XMm) * dx) + ((p.YMm - a.YMm) * dy)) / len2, 0, 1);
        return LayoutChecks.Distance(p, new PlanPoint(a.XMm + (t * dx), a.YMm + (t * dy)));
    }
}
