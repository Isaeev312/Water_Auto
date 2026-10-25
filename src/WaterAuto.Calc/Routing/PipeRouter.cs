using WaterAuto.Calc.Layout;
using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>
/// Трассировка по раскладке (этап 2c, ADR 0005): рядки — вдоль рядов раскладки, один питающий поперёк рядов,
/// подводящий от ввода — одним или двумя прямыми участками. Начальные DN — табл. Б.4 по числу оросителей за участком.
/// Что не удалось соединить, — замечание с координатами, а не молчание.
/// </summary>
public static class PipeRouter
{
    private const double Tol = Polygon.ToleranceMm;
    private const double SampleMm = 50;

    public static RoutingResult Route(FloorPlan plan, SprinklerLayout layout, INormsProvider norms, LayoutRules rules, Pressure designPressure)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rules);
        var routes = new List<PipeRoute>();
        var issues = new List<Issue>();
        var table = DnTable.Resolve(norms, designPressure);
        if (table is null)
        {
            issues.Add(new Issue("routing.noDnTable", IssueLevel.Error, "В данных норм нет табл. Б.4.", Clause: "табл. Б.4"));
            return new RoutingResult(routes, issues);
        }

        foreach (var room in plan.Rooms)
        {
            var sprinklers = layout.Sprinklers.Where(s => s.RoomId == room.Id).ToList();
            if (sprinklers.Count == 0)
            {
                continue;
            }

            var inlet = plan.Inlets.FirstOrDefault(i => room.Contour.Contains(i.Location));
            if (inlet is null)
            {
                issues.Add(new Issue("routing.noInlet", IssueLevel.Error, $"Помещение «{room.Name}»: внутри нет точки ПТ_ВВОД — трасса не строится. Соглашение: в каждом защищаемом помещении своя точка ПТ_ВВОД там, где в него входит питающий трубопровод (трасса между помещениями — позже).", room.Id, Location: room.Contour.Centroid));
                continue;
            }

            var angle = layout.Zones.FirstOrDefault(z => z.RoomId == room.Id)?.RowAngleRad ?? 0;
            var route = new RoomRouter(room, plan, sprinklers, inlet, angle, rules.MaxSpacing.Millimeters, table, issues).Build();
            if (route is not null)
            {
                routes.Add(route);
            }
        }

        return new RoutingResult(routes, issues);
    }

    private sealed record Run(double Y, List<(PlanPoint At, PlacedSprinkler S)> Items)
    {
        public double X0 => Items[0].At.XMm;

        public double X1 => Items[^1].At.XMm;
    }

    private readonly record struct Box(double X0, double X1, double Y0, double Y1, Column Column);

    /// <summary>Трасса одного помещения в системе координат, где ряды горизонтальны.</summary>
    private sealed class RoomRouter
    {
        private readonly Room _room;
        private readonly List<PlacedSprinkler> _sprinklers;
        private readonly Inlet _inlet;
        private readonly double _cos;
        private readonly double _sin;
        private readonly double _s;
        private readonly DnTable _table;
        private readonly List<Issue> _issues;
        private readonly List<PlanPoint> _contour;
        private readonly Polygon _contourPolygon;
        private readonly List<Polygon> _holes;
        private readonly List<Box> _columns;

        public RoomRouter(Room room, FloorPlan plan, List<PlacedSprinkler> sprinklers, Inlet inlet, double angle, double s, DnTable table, List<Issue> issues)
        {
            (_room, _sprinklers, _inlet, _s, _table, _issues) = (room, sprinklers, inlet, s, table, issues);
            (_cos, _sin) = angle == 0 ? (1.0, 0.0) : (Math.Cos(angle), Math.Sin(angle));
            _contour = room.Contour.Vertices.Select(Local).ToList();
            _contourPolygon = new Polygon(_contour);
            _holes = plan.NoSprinklerZones
                .Where(z => z.Outline.RelationTo(room.Contour) != PolygonRelation.Disjoint)
                .Select(z => new Polygon(z.Outline.Vertices.Select(Local).ToList()))
                .ToList();
            _columns = plan.Columns
                .Where(c => c.Outline.RelationTo(room.Contour) != PolygonRelation.Disjoint)
                .Select(c =>
                {
                    var v = c.Outline.Vertices.Select(Local).ToList();
                    return new Box(v.Min(p => p.XMm), v.Max(p => p.XMm), v.Min(p => p.YMm), v.Max(p => p.YMm), c);
                })
                .ToList();
        }

        public PipeRoute? Build()
        {
            var runs = Runs();
            var xs = Cluster(runs.SelectMany(r => r.Items.Select(i => i.At.XMm)));
            var inlet = Local(_inlet.Location);

            // Питающий — между соседними вертикалями оросителей или у концов рядов.
            var candidates = xs.Zip(xs.Skip(1), (a, b) => (a + b) / 2).Append(xs[0] - (_s / 4)).Append(xs[^1] + (_s / 4));
            var best = candidates
                .Select(x => Main(x, runs, inlet))
                .Where(m => m is not null)
                .Select(m => m!.Value)
                .OrderByDescending(m => m.Reached.Count)
                .ThenByDescending(m => m.Reached.Sum(r => r.Items.Count))
                .ThenBy(m => m.Imbalance)
                .ThenBy(m => Math.Abs(m.X - inlet.XMm))
                .FirstOrDefault();
            if (best.Reached is null)
            {
                _issues.Add(new Issue("routing.noMain", IssueLevel.Error, $"Помещение «{_room.Name}»: нет места для питающего трубопровода поперёк рядов.", _room.Id, Location: _room.Contour.Centroid));
                return null;
            }

            // Ряды, которые питающий не достал (их отрезали зоны запрета или контур), — дополнительные питающие,
            // каждый подключён к уже проложенному горизонтальной перемычкой по свободному коридору между рядами.
            var mains = new List<(double X, double Lo, double Hi, List<Run> Reached, int Imbalance)> { best };
            var links = new List<(int Parent, double Y)>();
            var remaining = runs.Where(r => !best.Reached.Contains(r)).ToList();
            while (remaining.Count > 0 && Extra(remaining, mains, inlet) is { } extra)
            {
                mains.Add(extra.Main);
                links.Add((extra.Parent, extra.Y));
                remaining.RemoveAll(extra.Main.Reached.Contains);
            }

            foreach (var run in remaining)
            {
                _issues.Add(new Issue(
                    "routing.rowNotReached",
                    IssueLevel.Error,
                    $"Помещение «{_room.Name}»: ряд из {run.Items.Count} оросителей не достигнут питающим — нужен второй питающий или ручная трасса.",
                    _room.Id,
                    Location: World(new PlanPoint((run.X0 + run.X1) / 2, run.Y))));
            }

            var rowYs = best.Reached.Select(r => r.Y).Order().ToList();
            var supply = Supply(best, rowYs, inlet, runs);
            if (supply is null)
            {
                _issues.Add(new Issue("routing.inletUnreachable", IssueLevel.Error, $"Помещение «{_room.Name}»: ввод нельзя соединить с питающим прямыми участками внутри помещения.", _inlet.Id, Location: _inlet.Location));
                return null;
            }

            return Assemble(mains, links, supply);
        }

        // Дополнительный питающий: больше достигнутых рядов, затем короче перемычка; перемычка не ложится на ряды.
        private ((double X, double Lo, double Hi, List<Run> Reached, int Imbalance) Main, int Parent, double Y)? Extra(
            List<Run> remaining, List<(double X, double Lo, double Hi, List<Run> Reached, int Imbalance)> mains, PlanPoint inlet)
        {
            var xs = Cluster(remaining.SelectMany(r => r.Items.Select(i => i.At.XMm)));
            var candidates = xs.Zip(xs.Skip(1), (a, b) => (a + b) / 2).Concat(remaining.SelectMany(r => new[] { r.X0 - (_s / 4), r.X1 + (_s / 4) }));
            var rowYs = mains.SelectMany(m => m.Reached).Concat(remaining).Select(r => r.Y).Distinct().Order().ToList();
            var corridors = rowYs.Zip(rowYs.Skip(1), (a, b) => (a + b) / 2).Append(rowYs[0] - (_s / 2)).Append(rowYs[^1] + (_s / 2)).ToList();
            ((double X, double Lo, double Hi, List<Run> Reached, int Imbalance) Main, int Parent, double Y)? best = null;
            var bestLength = double.PositiveInfinity;
            foreach (var x in candidates)
            {
                if (Main(x, remaining, inlet) is not { } m || (best is { } b && m.Reached.Count < b.Main.Reached.Count))
                {
                    continue;
                }

                for (var p = 0; p < mains.Count; p++)
                {
                    foreach (var y in corridors.Where(y => y > m.Lo + Tol && y < m.Hi - Tol && y > mains[p].Lo + Tol && y < mains[p].Hi - Tol))
                    {
                        var length = Math.Abs(mains[p].X - x);
                        if (length < Tol)
                        {
                            continue;
                        }

                        var better = best is null || m.Reached.Count > best.Value.Main.Reached.Count || length < bestLength;
                        if (better && Clear(new PlanPoint(mains[p].X, y), new PlanPoint(x, y)))
                        {
                            (best, bestLength) = ((m, p, y), length);
                        }
                    }
                }
            }

            return best;
        }

        // Ряды: оросители на одной горизонтали; ряд рвётся, где шаг больше нормы или труба вышла бы из помещения.
        private List<Run> Runs()
        {
            var points = _sprinklers.Select(s => (At: Local(s.Location), S: s)).OrderBy(p => p.At.YMm).ThenBy(p => p.At.XMm).ToList();
            var runs = new List<Run>();
            var lines = new List<List<(PlanPoint At, PlacedSprinkler S)>>();
            foreach (var p in points)
            {
                if (lines.Count == 0 || p.At.YMm - lines[^1][0].At.YMm > Tol)
                {
                    lines.Add([]);
                }

                lines[^1].Add(p);
            }

            foreach (var line in lines)
            {
                var sorted = line.OrderBy(p => p.At.XMm).ToList();
                var current = new List<(PlanPoint At, PlacedSprinkler S)> { sorted[0] };
                for (var k = 1; k < sorted.Count; k++)
                {
                    var (a, b) = (sorted[k - 1].At, sorted[k].At);
                    if (b.XMm - a.XMm > _s + Tol || !Clear(a, b))
                    {
                        runs.Add(new Run(current[0].At.YMm, current));
                        current = [];
                    }

                    current.Add(sorted[k]);
                }

                runs.Add(new Run(current[0].At.YMm, current));
            }

            return runs;
        }

        private (double X, double Lo, double Hi, List<Run> Reached, int Imbalance)? Main(double x, List<Run> runs, PlanPoint inlet)
        {
            (double Lo, double Hi, List<Run> Reached, int Imbalance)? best = null;
            foreach (var (lo, hi) in Slice(x))
            {
                var reached = runs.Where(r => r.Y > lo + Tol && r.Y < hi - Tol && Reaches(r, x)).ToList();
                if (reached.Count == 0)
                {
                    continue;
                }

                var imbalance = reached.Sum(r => Math.Abs(r.Items.Count(i => i.At.XMm < x) - r.Items.Count(i => i.At.XMm > x)));
                var holdsInlet = inlet.YMm > lo && inlet.YMm < hi;
                if (best is null || reached.Count > best.Value.Reached.Count
                    || (reached.Count == best.Value.Reached.Count && holdsInlet))
                {
                    best = (lo, hi, reached, imbalance);
                }
            }

            return best is { } b ? (x, b.Lo, b.Hi, b.Reached, b.Imbalance) : null;
        }

        // Питающий достаёт ряд, если проходит через него или рядом с концом (не дальше полшага) и подход к концу внутри помещения.
        private bool Reaches(Run r, double x) =>
            (x > r.X0 && x < r.X1)
            || (x <= r.X0 && r.X0 - x <= _s / 2 && Clear(new PlanPoint(x, r.Y), r.Items[0].At))
            || (x >= r.X1 && x - r.X1 <= _s / 2 && Clear(r.Items[^1].At, new PlanPoint(x, r.Y)));

        // Подводящий: ввод → (поворот) → точка на питающем. Не ложится на трубы рядов; меньше пересечений с рядами, затем короче.
        private List<PlanPoint>? Supply((double X, double Lo, double Hi, List<Run> Reached, int Imbalance) main, List<double> rowYs, PlanPoint inlet, List<Run> runs)
        {
            var ys = new List<double>();
            if (rowYs.All(y => Math.Abs(y - inlet.YMm) > Tol))
            {
                ys.Add(inlet.YMm);
            }

            ys.AddRange(rowYs.Zip(rowYs.Skip(1), (a, b) => (a + b) / 2));
            ys.Add((Math.Max(main.Lo, rowYs[0] - _s) + rowYs[0]) / 2);
            ys.Add((rowYs[^1] + Math.Min(main.Hi, rowYs[^1] + _s)) / 2);

            List<PlanPoint>? best = null;
            var bestCost = double.PositiveInfinity;
            foreach (var y in ys.Where(y => y > main.Lo + Tol && y < main.Hi - Tol))
            {
                var path = new List<PlanPoint> { inlet };
                if (Math.Abs(inlet.YMm - y) > Tol)
                {
                    path.Add(new PlanPoint(inlet.XMm, y));
                }

                if (Math.Abs(inlet.XMm - main.X) > Tol)
                {
                    path.Add(new PlanPoint(main.X, y));
                }

                var legs = path.Zip(path.Skip(1)).ToList();
                if (!legs.All(l => Clear(l.First, l.Second)))
                {
                    continue;
                }

                // Пересечения вертикального участка с рядами — трубы разных уровней; допускаются, но хуже.
                var crossings = runs.Count(r => r.Y > Math.Min(inlet.YMm, y) + Tol && r.Y < Math.Max(inlet.YMm, y) - Tol
                    && inlet.XMm > r.X0 - Tol && inlet.XMm < r.X1 + Tol);
                var cost = (crossings * 1e9) + legs.Sum(l => Dist(l.First, l.Second));
                if (cost < bestCost)
                {
                    (best, bestCost) = (path, cost);
                }
            }

            return best;
        }

        private PipeRoute Assemble(List<(double X, double Lo, double Hi, List<Run> Reached, int Imbalance)> mains, List<(int Parent, double Y)> links, List<PlanPoint> supply)
        {
            var nodes = new Dictionary<string, (RouteNodeKind Kind, PlanPoint At, string? Sprinkler)>();
            var edges = new List<(string A, string B, RouteSegmentRole Role)>();
            var tee = 0;
            string NewNode(RouteNodeKind kind, PlanPoint at, string prefix)
            {
                var id = $"{_room.Id}.{prefix}{++tee}";
                nodes[id] = (kind, at, null);
                return id;
            }

            nodes[_inlet.Id] = (RouteNodeKind.Inlet, supply[0], null);
            var junction = _inlet.Id;
            var prev = _inlet.Id;
            for (var k = 1; k < supply.Count; k++)
            {
                var id = k == supply.Count - 1 ? NewNode(RouteNodeKind.Tee, supply[k], "T") : NewNode(RouteNodeKind.Bend, supply[k], "B");
                edges.Add((prev, id, RouteSegmentRole.Supply));
                prev = junction = id;
            }

            // Питающие: узлы на рядах, узел подводящего и концы перемычек, по порядку вдоль каждого питающего.
            var junctionY = nodes[junction].At.YMm;
            var mainNodes = mains.Select(_ => new List<(double Y, string Id)>()).ToList();
            mainNodes[0].Add((junctionY, junction));
            // Узел на питающем m на отметке y; совпадающий по месту узел используется повторно (без участков нулевой длины).
            string On(int m, double y)
            {
                var existing = mainNodes[m].FirstOrDefault(n => Math.Abs(n.Y - y) < Tol);
                if (existing.Id is not null)
                {
                    return existing.Id;
                }

                var id = NewNode(RouteNodeKind.Tee, new PlanPoint(mains[m].X, y), "T");
                mainNodes[m].Add((y, id));
                return id;
            }

            var rowTees = new Dictionary<Run, string>();
            for (var m = 0; m < mains.Count; m++)
            {
                foreach (var run in mains[m].Reached)
                {
                    rowTees[run] = On(m, run.Y);
                }
            }

            for (var k = 0; k < links.Count; k++)
            {
                var (parent, y) = links[k];
                edges.Add((On(parent, y), On(k + 1, y), RouteSegmentRole.Main));
            }

            for (var m = 0; m < mains.Count; m++)
            {
                var ordered = mainNodes[m].OrderBy(n => n.Y).ToList();
                for (var k = 1; k < ordered.Count; k++)
                {
                    edges.Add((ordered[k - 1].Id, ordered[k].Id, RouteSegmentRole.Main));
                }
            }

            // Рядки: от узла на питающем в обе стороны по порядку.
            foreach (var main in mains)
            {
                foreach (var run in main.Reached)
                {
                    foreach (var side in new[] { run.Items.Where(i => i.At.XMm < main.X).OrderByDescending(i => i.At.XMm), run.Items.Where(i => i.At.XMm > main.X).OrderBy(i => i.At.XMm) })
                    {
                        var from = rowTees[run];
                        foreach (var (at, s) in side)
                        {
                            nodes[s.Id] = (RouteNodeKind.Sprinkler, at, s.Id);
                            edges.Add((from, s.Id, RouteSegmentRole.Row));
                            from = s.Id;
                        }
                    }
                }
            }

            WarnColumns(edges, nodes);
            return Orient(nodes, edges);
        }

        // Направление участков — от ввода; число оросителей за участком и начальный DN.
        private PipeRoute Orient(Dictionary<string, (RouteNodeKind Kind, PlanPoint At, string? Sprinkler)> nodes, List<(string A, string B, RouteSegmentRole Role)> edges)
        {
            var adjacent = nodes.Keys.ToDictionary(k => k, _ => new List<(string Other, RouteSegmentRole Role)>());
            foreach (var (a, b, role) in edges)
            {
                adjacent[a].Add((b, role));
                adjacent[b].Add((a, role));
            }

            var order = new List<(string From, string To, RouteSegmentRole Role)>();
            var seen = new HashSet<string> { _inlet.Id };
            var stack = new Stack<string>([_inlet.Id]);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                foreach (var (other, role) in adjacent[id].AsEnumerable().Reverse())
                {
                    if (seen.Add(other))
                    {
                        order.Add((id, other, role));
                        stack.Push(other);
                    }
                }
            }

            var fed = new Dictionary<string, int>();
            int Fed(string id) => fed.TryGetValue(id, out var f) ? f
                : fed[id] = (nodes[id].Kind == RouteNodeKind.Sprinkler ? 1 : 0) + order.Where(o => o.From == id).Sum(o => Fed(o.To));

            var segments = order
                .Select((o, k) =>
                {
                    var n = Fed(o.To);
                    var length = Length.FromMillimeters(Dist(nodes[o.From].At, nodes[o.To].At));
                    return new RouteSegment($"{_room.Id}.P{k + 1}", o.From, o.To, o.Role, length, n, _table.Pick(n));
                })
                .ToList();
            var routeNodes = nodes
                .Where(n => seen.Contains(n.Key))
                .Select(n => new RouteNode(n.Key, n.Value.Kind, n.Key == _inlet.Id ? _inlet.Location : World(n.Value.At), n.Value.Sprinkler))
                .ToList();
            return new PipeRoute(_room.Id, _inlet.Id, routeNodes, segments);
        }

        private void WarnColumns(List<(string A, string B, RouteSegmentRole Role)> edges, Dictionary<string, (RouteNodeKind Kind, PlanPoint At, string? Sprinkler)> nodes)
        {
            foreach (var column in _columns)
            {
                if (edges.Any(e => Through(column, nodes[e.A].At, nodes[e.B].At)))
                {
                    _issues.Add(new Issue(
                        "routing.throughColumn",
                        IssueLevel.Warning,
                        "Трубопровод проходит через колонну — при оформлении обойти её или сместить трубу.",
                        column.Column.Id,
                        Location: column.Column.Outline.Centroid));
                }
            }
        }

        // Участки вдоль осей: проходит ли отрезок сквозь сечение колонны.
        private static bool Through(Box c, PlanPoint a, PlanPoint b)
        {
            double x0 = Math.Min(a.XMm, b.XMm), x1 = Math.Max(a.XMm, b.XMm), y0 = Math.Min(a.YMm, b.YMm), y1 = Math.Max(a.YMm, b.YMm);
            return x1 > c.X0 + Tol && x0 < c.X1 - Tol && y1 > c.Y0 + Tol && y0 < c.Y1 - Tol;
        }

        // Отрезок внутри помещения и вне зон запрета (проверка точками через 50 мм).
        private bool Clear(PlanPoint a, PlanPoint b)
        {
            var n = Math.Max(1, (int)Math.Ceiling(Dist(a, b) / SampleMm));
            for (var k = 0; k <= n; k++)
            {
                var p = new PlanPoint(a.XMm + (k * (b.XMm - a.XMm) / n), a.YMm + (k * (b.YMm - a.YMm) / n));
                if (!_contourPolygon.Contains(p) || _holes.Any(h => StrictlyInside(h, p)))
                {
                    return false;
                }
            }

            return true;
        }

        // Интервалы вертикали x внутри помещения, за вычетом зон запрета и колонн.
        private List<(double Lo, double Hi)> Slice(double x)
        {
            var result = Crossings(_contour, x);
            foreach (var cut in _holes.SelectMany(h => Crossings(h.Vertices.ToList(), x))
                .Concat(_columns.Where(c => x >= c.X0 - Tol && x <= c.X1 + Tol).Select(c => (c.Y0 - Tol, c.Y1 + Tol))))
            {
                result = result.SelectMany(iv => Subtract(iv, cut)).ToList();
            }

            return result;
        }

        private static IEnumerable<(double, double)> Subtract((double Lo, double Hi) iv, (double Lo, double Hi) cut)
        {
            if (cut.Hi <= iv.Lo || cut.Lo >= iv.Hi)
            {
                yield return iv;
                yield break;
            }

            if (cut.Lo > iv.Lo)
            {
                yield return (iv.Lo, cut.Lo);
            }

            if (cut.Hi < iv.Hi)
            {
                yield return (cut.Hi, iv.Hi);
            }
        }

        // Чётно-нечётное правило по горизонтальным сторонам, пересекающим вертикаль x.
        private static List<(double Lo, double Hi)> Crossings(List<PlanPoint> poly, double x)
        {
            var ys = poly
                .Select((p, i) => (A: p, B: poly[(i + 1) % poly.Count]))
                .Where(e => Math.Abs(e.A.YMm - e.B.YMm) <= Tol && Math.Min(e.A.XMm, e.B.XMm) < x && Math.Max(e.A.XMm, e.B.XMm) > x)
                .Select(e => e.A.YMm)
                .Order()
                .ToList();
            var result = new List<(double, double)>();
            for (var i = 0; i + 1 < ys.Count; i += 2)
            {
                result.Add((ys[i], ys[i + 1]));
            }

            return result;
        }

        private static bool StrictlyInside(Polygon polygon, PlanPoint p) =>
            polygon.Contains(p) && polygon.Edges().All(e => DistanceToSegment(p, e.A, e.B) > Tol);

        private static List<double> Cluster(IEnumerable<double> values)
        {
            var result = new List<double>();
            foreach (var v in values.Order())
            {
                if (result.Count == 0 || v - result[^1] > Tol)
                {
                    result.Add(v);
                }
            }

            return result;
        }

        private PlanPoint Local(PlanPoint p) => new((p.XMm * _cos) + (p.YMm * _sin), (-p.XMm * _sin) + (p.YMm * _cos));

        private PlanPoint World(PlanPoint p) => new((p.XMm * _cos) - (p.YMm * _sin), (p.XMm * _sin) + (p.YMm * _cos));

        private static double Dist(PlanPoint a, PlanPoint b) => Math.Sqrt(((a.XMm - b.XMm) * (a.XMm - b.XMm)) + ((a.YMm - b.YMm) * (a.YMm - b.YMm)));

        private static double DistanceToSegment(PlanPoint p, PlanPoint a, PlanPoint b)
        {
            double dx = b.XMm - a.XMm, dy = b.YMm - a.YMm;
            var len2 = (dx * dx) + (dy * dy);
            var t = len2 == 0 ? 0 : Math.Clamp((((p.XMm - a.XMm) * dx) + ((p.YMm - a.YMm) * dy)) / len2, 0, 1);
            return Dist(p, new PlanPoint(a.XMm + (t * dx), a.YMm + (t * dy)));
        }
    }
}
