using WaterAuto.Calc.Layout;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

/// <summary>
/// Независимая проверка раскладки по 6.2.21 — без знания алгоритма: из каждого оросителя по осям сетки
/// до соседа не больше шага, а если соседа нет — до стены не больше предела; соседи не ближе наименьшего шага.
/// </summary>
internal static class LayoutChecks
{
    public static void ShouldSatisfy(SprinklerLayout layout, FloorPlan plan, LayoutRules rules, double angleRad = 0, double lateralToleranceMm = 1)
    {
        var s = rules.MaxSpacing.Millimeters + 0.5;
        var m = rules.MinSpacing.Millimeters - 0.5;
        var wall = rules.MaxWallDistance.Millimeters + 0.5;
        var u = (X: Math.Cos(angleRad), Y: Math.Sin(angleRad));
        var directions = new[] { u, (-u.X, -u.Y), (-u.Y, u.X), (u.Y, -u.X) };

        foreach (var room in plan.Rooms)
        {
            var points = layout.Sprinklers.Where(p => p.RoomId == room.Id).Select(p => p.Location).ToList();
            var walls = room.Contour.Edges().Concat(plan.NoSprinklerZones.SelectMany(z => z.Outline.Edges())).ToList();
            foreach (var p in points)
            {
                room.Contour.Contains(p).ShouldBeTrue($"{p} вне помещения {room.Name}");
                plan.Columns.Any(c => c.Outline.Contains(p)).ShouldBeFalse($"{p} в колонне");
                plan.NoSprinklerZones.Any(z => z.Outline.Contains(p)).ShouldBeFalse($"{p} в зоне запрета");
                foreach (var q in points.Where(q => q != p))
                {
                    Distance(p, q).ShouldBeGreaterThanOrEqualTo(m, $"{p} и {q} ближе наименьшего шага");
                }

                foreach (var d in directions)
                {
                    var toWall = walls.Select(e => Ray(p, d, e.A, e.B)).Where(t => t > 1e-6).DefaultIfEmpty(double.PositiveInfinity).Min();
                    var ahead = points
                        .Select(q => (Q: q, Along: ((q.XMm - p.XMm) * d.Item1) + ((q.YMm - p.YMm) * d.Item2), Lateral: Math.Abs(((q.XMm - p.XMm) * d.Item2) - ((q.YMm - p.YMm) * d.Item1))))
                        .Where(x => x.Along > 1 && x.Lateral <= lateralToleranceMm && x.Along < toWall)
                        .OrderBy(x => x.Along)
                        .FirstOrDefault();
                    if (ahead.Along > 0)
                    {
                        Distance(p, ahead.Q).ShouldBeLessThanOrEqualTo(s, $"от {p} до соседа {ahead.Q} больше шага");
                    }
                    else
                    {
                        toWall.ShouldBeLessThanOrEqualTo(wall, $"от {p} до стены больше предела ({d.Item1:0.##}, {d.Item2:0.##})");
                    }
                }
            }
        }
    }

    public static double Distance(PlanPoint a, PlanPoint b) => Math.Sqrt(((a.XMm - b.XMm) * (a.XMm - b.XMm)) + ((a.YMm - b.YMm) * (a.YMm - b.YMm)));

    // Расстояние по лучу p + t·d до отрезка ab (∞, если не пересекает).
    private static double Ray(PlanPoint p, (double X, double Y) d, PlanPoint a, PlanPoint b)
    {
        double ex = b.XMm - a.XMm, ey = b.YMm - a.YMm;
        var den = (d.X * ey) - (d.Y * ex);
        if (Math.Abs(den) < 1e-12)
        {
            return double.PositiveInfinity;
        }

        double wx = a.XMm - p.XMm, wy = a.YMm - p.YMm;
        var t = ((wx * ey) - (wy * ex)) / den;
        var k = ((wx * d.Y) - (wy * d.X)) / den;
        return k is >= -1e-9 and <= 1 + 1e-9 ? t : double.PositiveInfinity;
    }
}
