using WaterAuto.Core;
using WaterAuto.Export;

namespace WaterAuto.Application;

/// <summary>
/// Привязки оросителей для плана: в каждом помещении — цепочка вдоль нижнего ряда (стена — оросители — стена)
/// и вдоль левого столбца. Только для помещений с рядами по осям чертежа; повёрнутые помещения пропускаются.
/// </summary>
public static class PlanDimensions
{
    private const double Tolerance = 50;

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
}
