using WaterAuto.Core;
using WaterAuto.Export;

namespace WaterAuto.Application;

/// <summary>
/// Привязки для плана. <see cref="Build"/> — в каждом помещении цепочка вдоль нижнего ряда (стена — оросители — стена)
/// и вдоль левого столбца. <see cref="Ties"/> — привязки к разбивочным осям по колоннам: цепочки «оси + рядки» и
/// «оси + столбцы» снаружи помещения, шаги оросителей в каждом рядке, питающие и подводящий трубопроводы, ввод
/// и пожарные краны. Только для помещений с рядами по осям чертежа; повёрнутые помещения пропускаются.
/// </summary>
public static class PlanDimensions
{
    private const double Tolerance = 50;

    // Буквы осей по ГОСТ 21.101: без Ё, З, Й, О, Х, Ц, Ч, Щ, Ъ, Ы, Ь.
    private const string AxisLetters = "АБВГДЕЖИКЛМНПРСТУФШЭЮЯ";

    public static IReadOnlyList<PlanDimensionChain> Build(FloorPlan plan, SprinklerLayout layout, double offsetMm = 600)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(layout);
        var chains = new List<PlanDimensionChain>();
        foreach (var room in plan.Rooms)
        {
            if (layout.Zones.Any(z => z.RoomId == room.Id && Math.Abs(z.RowAngleRad) > 1e-6))
            {
                continue;
            }

            var points = layout.Sprinklers.Where(s => s.RoomId == room.Id && s.UnderObstacleId is null).Select(s => s.Location).ToList();
            if (points.Count < 2)
            {
                continue;
            }

            var row = Lowest(points, p => p.YMm, p => p.XMm);
            if (row.Count >= 1 && Walls(room.Contour, row[0].YMm, row[0].XMm, row[^1].XMm, horizontal: true) is var (x0, x1))
            {
                chains.Add(new PlanDimensionChain([new(x0, row[0].YMm), .. row, new(x1, row[0].YMm)], -offsetMm));
            }

            var column = Lowest(points, p => p.XMm, p => p.YMm);
            if (column.Count >= 1 && Walls(room.Contour, column[0].XMm, column[0].YMm, column[^1].YMm, horizontal: false) is var (y0, y1))
            {
                chains.Add(new PlanDimensionChain([new(column[0].XMm, y0), .. column, new(column[0].XMm, y1)], offsetMm));
            }
        }

        return chains;
    }

    // Ряд с наименьшей координатой key, отсортированный по along.
    private static List<PlanPoint> Lowest(List<PlanPoint> points, Func<PlanPoint, double> key, Func<PlanPoint, double> along)
    {
        var min = points.Min(key);
        return points.Where(p => key(p) - min < Tolerance).OrderBy(along).ToList();
    }

    // Ближайшие стены по линии (горизонтальной y = level или вертикальной x = level) слева от from и справа от to.
    private static (double Lo, double Hi)? Walls(Polygon contour, double level, double from, double to, bool horizontal)
    {
        var v = contour.Vertices;
        var hits = new List<double>();
        for (var i = 0; i < v.Count; i++)
        {
            var (a, b) = (v[i], v[(i + 1) % v.Count]);
            var (ca, cb, la, lb) = horizontal ? (a.YMm, b.YMm, a.XMm, b.XMm) : (a.XMm, b.XMm, a.YMm, b.YMm);
            if ((ca - level) * (cb - level) < 0 || (Math.Abs(ca - cb) > 1e-9 && (Math.Abs(ca - level) < 1e-9 || Math.Abs(cb - level) < 1e-9)))
            {
                hits.Add(la + ((lb - la) * (level - ca) / (cb - ca)));
            }
        }

        var lo = hits.Where(h => h < from - 1).DefaultIfEmpty(double.NaN).Max();
        var hi = hits.Where(h => h > to + 1).DefaultIfEmpty(double.NaN).Min();
        return double.IsNaN(lo) || double.IsNaN(hi) ? null : (lo, hi);
    }

    /// <summary>
    /// Оси по колоннам: координата центра, общая не менее чем для двух колонн. Вертикальные (постоянный X) — цифры слева
    /// направо, горизонтальные — буквы снизу вверх.
    /// </summary>
    public static IReadOnlyList<PlanAxis> Axes(FloorPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var centers = plan.Columns.Select(c => c.Outline.Centroid).ToList();
        List<double> Lines(Func<PlanPoint, double> key) => [.. Cluster(centers.Select(key)).Where(g => g.Count >= 2).Select(g => g.Average())];
        var axes = new List<PlanAxis>();
        axes.AddRange(Lines(p => p.XMm).Select((x, k) => new PlanAxis((k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Vertical: true, x)));
        axes.AddRange(Lines(p => p.YMm).Select((y, k) => new PlanAxis(k < AxisLetters.Length ? AxisLetters[k].ToString() : $"{AxisLetters[k % AxisLetters.Length]}{k / AxisLetters.Length}", Vertical: false, y)));
        return axes;
    }

    /// <summary>Привязки к осям и шаги в рядках (см. описание класса). Без колонн привязок к осям нет — только шаги в рядках.</summary>
    public static IReadOnlyList<PlanDimensionChain> Ties(FloorPlan plan, SprinklerLayout layout, IReadOnlyList<PipeRoute> routes, IReadOnlyList<PlanPoint> hydrants, double offsetMm = 1500, double rowOffsetMm = 300)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(hydrants);
        var axes = Axes(plan);
        var xs = axes.Where(a => a.Vertical).Select(a => a.Position).ToList();
        var ys = axes.Where(a => !a.Vertical).Select(a => a.Position).ToList();
        var chains = new List<PlanDimensionChain>();
        foreach (var room in plan.Rooms)
        {
            if (layout.Zones.Any(z => z.RoomId == room.Id && Math.Abs(z.RowAngleRad) > 1e-6))
            {
                continue;
            }

            var points = layout.Sprinklers.Where(s => s.RoomId == room.Id && s.UnderObstacleId is null).Select(s => s.Location).ToList();
            if (points.Count == 0)
            {
                continue;
            }

            var (minX, minY) = (room.Contour.Vertices.Min(v => v.XMm), room.Contour.Vertices.Min(v => v.YMm));

            // Рядки и столбцы оросителей вместе с осями — цепочки снаружи левой и нижней стен.
            var chainY = Cluster(Cluster(points.Select(p => p.YMm)).Select(g => g.Average()).Concat(ys)).Select(g => g.Average()).ToList();
            var chainX = Cluster(Cluster(points.Select(p => p.XMm)).Select(g => g.Average()).Concat(xs)).Select(g => g.Average()).ToList();
            if (ys.Count > 0 && chainY.Count >= 2)
            {
                chains.Add(new PlanDimensionChain([.. chainY.Select(y => new PlanPoint(minX, y))], offsetMm));
            }

            if (xs.Count > 0 && chainX.Count >= 2)
            {
                chains.Add(new PlanDimensionChain([.. chainX.Select(x => new PlanPoint(x, minY))], -offsetMm));
            }

            // Шаги оросителей в каждом рядке — под рядком; нижний рядок уже в цепочке до стен (Build).
            foreach (var row in Cluster(points.Select(p => p.YMm)).Skip(1))
            {
                var level = row.Average();
                var line = points.Where(p => Math.Abs(p.YMm - level) < Tolerance).OrderBy(p => p.XMm).ToList();
                if (line.Count >= 2)
                {
                    chains.Add(new PlanDimensionChain(line, -rowOffsetMm));
                }
            }
        }

        // Питающие и подводящий: прямой отрезок по оси чертежа — привязка к ближайшей параллельной оси у его середины.
        var tied = new HashSet<(bool, long)>();
        foreach (var route in routes)
        {
            var nodes = route.Nodes.ToDictionary(n => n.Id);
            foreach (var s in route.Segments.Where(s => s.Role != RouteSegmentRole.Row))
            {
                var (a, b) = (nodes[s.From].Location, nodes[s.To].Location);
                var vertical = Math.Abs(a.XMm - b.XMm) < 1;
                var horizontal = Math.Abs(a.YMm - b.YMm) < 1;
                if (vertical == horizontal)
                {
                    continue;
                }

                var level = vertical ? a.XMm : a.YMm;
                if (Nearest(vertical ? xs : ys, level) is not { } axis || Math.Abs(axis - level) < 1 || !tied.Add((vertical, (long)Math.Round(level))))
                {
                    continue;
                }

                var mid = new PlanPoint((a.XMm + b.XMm) / 2, (a.YMm + b.YMm) / 2);
                chains.Add(vertical
                    ? new PlanDimensionChain([new(level, mid.YMm), new(axis, mid.YMm)], 0)
                    : new PlanDimensionChain([new(mid.XMm, level), new(mid.XMm, axis)], 0));
            }
        }

        // Ввод и пожарные краны — к ближайшим осям по X и по Y.
        foreach (var p in plan.Inlets.Select(i => i.Location).Concat(hydrants))
        {
            if (Nearest(xs, p.XMm) is { } ax && Math.Abs(ax - p.XMm) >= 1)
            {
                chains.Add(new PlanDimensionChain([p, new(ax, p.YMm)], 0));
            }

            if (Nearest(ys, p.YMm) is { } ay && Math.Abs(ay - p.YMm) >= 1)
            {
                chains.Add(new PlanDimensionChain([p, new(p.XMm, ay)], 0));
            }
        }

        return chains;
    }

    private static double? Nearest(List<double> lines, double value) => lines.Count == 0 ? null : lines.MinBy(l => Math.Abs(l - value));

    // Группы близких значений (допуск Tolerance), по возрастанию.
    private static List<List<double>> Cluster(IEnumerable<double> values)
    {
        var groups = new List<List<double>>();
        foreach (var v in values.Order())
        {
            if (groups.Count > 0 && v - groups[^1][^1] < Tolerance)
            {
                groups[^1].Add(v);
            }
            else
            {
                groups.Add([v]);
            }
        }

        return groups;
    }
}
