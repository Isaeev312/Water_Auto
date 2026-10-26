using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Трасса ввода: ломаная в плане, длина, мм, число поворотов (отводов) в плане.</summary>
public sealed record InletRoute(IReadOnlyList<PlanPoint> Path, double LengthMm, int Elbows, IReadOnlyList<Issue> Issues);

/// <summary>
/// Трасса от точки ввода в здание до точки ввода в насосную: поиск по сетке (шаг 250 мм) с ходами по осям чертежа;
/// поворот «стоит» как 4 м трубы — путь с наименьшим числом отводов. Проходимо — внутри помещений, не ближе отступа
/// к колоннам и не внутри насосной (конечная точка — на её стене, подход снаружи).
/// </summary>
public static class InletRouting
{
    private const double Step = 250;
    private const double TurnCost = 4000;

    /// <param name="avoid">Уже проложенные трассы: их не пересекать и не подходить ближе отступа (кроме участков у концов).</param>
    public static InletRoute Route(FloorPlan plan, PlanPoint from, PlanPoint to, double clearanceMm, IReadOnlyList<IReadOnlyList<PlanPoint>>? avoid = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var all = plan.Rooms.SelectMany(r => r.Contour.Vertices).ToList();
        var (minX, minY) = (all.Min(p => p.XMm) - Step, all.Min(p => p.YMm) - Step);
        var nx = (int)Math.Ceiling((all.Max(p => p.XMm) + Step - minX) / Step) + 1;
        var ny = (int)Math.Ceiling((all.Max(p => p.YMm) + Step - minY) / Step) + 1;
        PlanPoint C(int i, int j) => new(minX + (i * Step), minY + (j * Step));
        (int I, int J) Cell(PlanPoint p) => ((int)Math.Round((p.XMm - minX) / Step), (int)Math.Round((p.YMm - minY) / Step));

        var columns = plan.Columns.Select(c => (
            X0: c.Outline.Vertices.Min(v => v.XMm) - clearanceMm,
            X1: c.Outline.Vertices.Max(v => v.XMm) + clearanceMm,
            Y0: c.Outline.Vertices.Min(v => v.YMm) - clearanceMm,
            Y1: c.Outline.Vertices.Max(v => v.YMm) + clearanceMm)).ToList();
        var pump = plan.PumpRooms.Select(r => r.Outline).ToList();
        bool InsidePump(PlanPoint p) => pump.Any(o => o.Contains(p) && o.DistanceTo(p) > 1);
        var free = new bool[nx, ny];
        for (var i = 0; i < nx; i++)
        {
            for (var j = 0; j < ny; j++)
            {
                var p = C(i, j);
                free[i, j] = plan.Rooms.Any(r => r.Contour.Contains(p))
                    && !columns.Any(c => p.XMm > c.X0 && p.XMm < c.X1 && p.YMm > c.Y0 && p.YMm < c.Y1)
                    && !InsidePump(p)
                    && !(avoid ?? []).Any(path => path.Zip(path.Skip(1)).Any(s => Near(p, s.First, s.Second) < clearanceMm + Step));
            }
        }

        // Старт и финиш — ближайшие свободные узлы сетки к точкам на стенах.
        (int I, int J)? Nearest(PlanPoint p)
        {
            var (ci, cj) = Cell(p);
            for (var rad = 0; rad < 6; rad++)
            {
                for (var di = -rad; di <= rad; di++)
                {
                    for (var dj = -rad; dj <= rad; dj++)
                    {
                        var (i, j) = (ci + di, cj + dj);
                        if (Math.Max(Math.Abs(di), Math.Abs(dj)) == rad && i >= 0 && j >= 0 && i < nx && j < ny && free[i, j])
                        {
                            return (i, j);
                        }
                    }
                }
            }

            return null;
        }

        var fail = new InletRoute([from, to], 0, 0, [new Issue("inlet.noRoute", IssueLevel.Error, "Трасса от ввода в здание до насосной не найдена: проверьте, что точки стоят на стенах и проход не перекрыт.", null, null, to)]);
        if (InsidePump(to) || Nearest(from) is not { } s || Nearest(to) is not { } g)
        {
            return fail;
        }

        // A* по состояниям (узел, направление).
        int[] dx = [1, 0, -1, 0];
        int[] dy = [0, 1, 0, -1];
        var cost = new Dictionary<(int, int, int), double>();
        var prev = new Dictionary<(int, int, int), (int, int, int)>();
        var open = new PriorityQueue<(int I, int J, int D), double>();
        for (var d = 0; d < 4; d++)
        {
            cost[(s.I, s.J, d)] = 0;
            open.Enqueue((s.I, s.J, d), 0);
        }

        (int I, int J, int D)? end = null;
        while (open.TryDequeue(out var cur, out _))
        {
            if (cur.I == g.I && cur.J == g.J)
            {
                end = cur;
                break;
            }

            var c0 = cost[cur];
            for (var d = 0; d < 4; d++)
            {
                if (d == (cur.D + 2) % 4)
                {
                    continue;
                }

                var (i, j) = (cur.I + dx[d], cur.J + dy[d]);
                if (i < 0 || j < 0 || i >= nx || j >= ny || !free[i, j])
                {
                    continue;
                }

                var nc = c0 + Step + (d == cur.D ? 0 : TurnCost);
                if (!cost.TryGetValue((i, j, d), out var old) || nc < old)
                {
                    cost[(i, j, d)] = nc;
                    prev[(i, j, d)] = cur;
                    open.Enqueue((i, j, d), nc + ((Math.Abs(i - g.I) + Math.Abs(j - g.J)) * Step));
                }
            }
        }

        if (end is not { } e)
        {
            return fail;
        }

        var cells = new List<(int I, int J)>();
        var k = (e.I, e.J, e.D);
        while (true)
        {
            cells.Add((k.I, k.J));
            if (!prev.TryGetValue(k, out var p))
            {
                break;
            }

            k = p;
        }

        cells.Reverse();

        // Ломаная: точка на стене, выход на ось сетки перпендикулярно, углы, подход к стене насосной.
        var pts = new List<PlanPoint> { from };
        var first = C(cells[0].I, cells[0].J);
        pts.Add(Math.Abs(first.XMm - from.XMm) < Math.Abs(first.YMm - from.YMm) ? new PlanPoint(first.XMm, from.YMm) : new PlanPoint(from.XMm, first.YMm));
        pts.AddRange(cells.Select(c => C(c.I, c.J)));
        var last = C(cells[^1].I, cells[^1].J);
        pts.Add(Math.Abs(last.XMm - to.XMm) < Math.Abs(last.YMm - to.YMm) ? new PlanPoint(last.XMm, to.YMm) : new PlanPoint(to.XMm, last.YMm));
        pts.Add(to);
        var path = Simplify(pts);
        var length = path.Zip(path.Skip(1), (a, b) => Math.Abs(b.XMm - a.XMm) + Math.Abs(b.YMm - a.YMm)).Sum();
        return new InletRoute(path, length, Math.Max(0, path.Count - 2), []);
    }

    // Расстояние от точки до отрезка, мм.
    private static double Near(PlanPoint p, PlanPoint a, PlanPoint b)
    {
        double vx = b.XMm - a.XMm, vy = b.YMm - a.YMm;
        var len2 = (vx * vx) + (vy * vy);
        var t = len2 < 1e-9 ? 0 : Math.Clamp((((p.XMm - a.XMm) * vx) + ((p.YMm - a.YMm) * vy)) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(p.XMm - (a.XMm + (t * vx)), 2) + Math.Pow(p.YMm - (a.YMm + (t * vy)), 2));
    }

    // Без повторов и промежуточных точек на одной прямой; косые сшивки заменяются ступенькой по осям.
    private static List<PlanPoint> Simplify(List<PlanPoint> pts)
    {
        var res = new List<PlanPoint>();
        foreach (var p in pts)
        {
            if (res.Count > 0 && Math.Abs(res[^1].XMm - p.XMm) < 1 && Math.Abs(res[^1].YMm - p.YMm) < 1)
            {
                continue;
            }

            if (res.Count > 0 && Math.Abs(res[^1].XMm - p.XMm) > 1 && Math.Abs(res[^1].YMm - p.YMm) > 1)
            {
                res.Add(new PlanPoint(p.XMm, res[^1].YMm));
            }

            res.Add(p);
        }

        for (var i = res.Count - 2; i >= 1; i--)
        {
            var (a, b, c) = (res[i - 1], res[i], res[i + 1]);
            if ((Math.Abs(a.XMm - b.XMm) < 1 && Math.Abs(b.XMm - c.XMm) < 1) || (Math.Abs(a.YMm - b.YMm) < 1 && Math.Abs(b.YMm - c.YMm) < 1))
            {
                res.RemoveAt(i);
            }
        }

        return res;
    }
}
