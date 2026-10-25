using System.Globalization;

namespace WaterAuto.Core;

/// <summary>Точка плана, мм (единицы чертежа — README §7).</summary>
public readonly record struct PlanPoint(double XMm, double YMm)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"X {XMm:0}, Y {YMm:0} мм");
}

public enum PolygonRelation
{
    Disjoint,
    Intersecting,
    FirstInsideSecond,
    SecondInsideFirst,
}

/// <summary>Замкнутый многоугольник плана (вершины без повтора первой в конце), мм.</summary>
public sealed record Polygon(IReadOnlyList<PlanPoint> Vertices)
{
    /// <summary>Допуск геометрических проверок, мм.</summary>
    public const double ToleranceMm = 1.0;

    public double AreaMm2 => Math.Abs(SignedArea());

    public PlanPoint Centroid
    {
        get
        {
            double a = SignedArea(), cx = 0, cy = 0;
            if (Math.Abs(a) < 1e-9)
            {
                return new PlanPoint(Vertices.Average(v => v.XMm), Vertices.Average(v => v.YMm));
            }

            for (var i = 0; i < Vertices.Count; i++)
            {
                var p = Vertices[i];
                var q = Vertices[(i + 1) % Vertices.Count];
                var cross = (p.XMm * q.YMm) - (q.XMm * p.YMm);
                cx += (p.XMm + q.XMm) * cross;
                cy += (p.YMm + q.YMm) * cross;
            }

            return new PlanPoint(cx / (6 * a), cy / (6 * a));
        }
    }

    public IEnumerable<(PlanPoint A, PlanPoint B)> Edges()
    {
        for (var i = 0; i < Vertices.Count; i++)
        {
            yield return (Vertices[i], Vertices[(i + 1) % Vertices.Count]);
        }
    }

    /// <summary>Точка внутри или на границе (с допуском).</summary>
    public bool Contains(PlanPoint p)
    {
        if (Edges().Any(e => DistanceToSegment(p, e.A, e.B) <= ToleranceMm))
        {
            return true;
        }

        var inside = false;
        foreach (var (a, b) in Edges())
        {
            if ((a.YMm > p.YMm) != (b.YMm > p.YMm)
                && p.XMm < ((b.XMm - a.XMm) * (p.YMm - a.YMm) / (b.YMm - a.YMm)) + a.XMm)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Есть ли пересечение несмежных сторон (самопересечение контура).</summary>
    public bool IsSelfIntersecting()
    {
        var edges = Edges().ToList();
        for (var i = 0; i < edges.Count; i++)
        {
            for (var j = i + 2; j < edges.Count; j++)
            {
                if (i == 0 && j == edges.Count - 1)
                {
                    continue; // первая и последняя стороны смежны
                }

                if (SegmentsCross(edges[i].A, edges[i].B, edges[j].A, edges[j].B))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public PolygonRelation RelationTo(Polygon other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (a, b) in Edges())
        {
            foreach (var (c, d) in other.Edges())
            {
                if (SegmentsCross(a, b, c, d))
                {
                    return PolygonRelation.Intersecting;
                }
            }
        }

        if (Vertices.All(other.Contains))
        {
            return PolygonRelation.FirstInsideSecond;
        }

        if (other.Vertices.All(Contains))
        {
            return PolygonRelation.SecondInsideFirst;
        }

        return Vertices.Any(other.Contains) || other.Vertices.Any(Contains) ? PolygonRelation.Intersecting : PolygonRelation.Disjoint;
    }

    private double SignedArea()
    {
        var s = 0.0;
        foreach (var (a, b) in Edges())
        {
            s += (a.XMm * b.YMm) - (b.XMm * a.YMm);
        }

        return s / 2;
    }

    // Собственное пересечение отрезков: концы строго по разные стороны (касание и наложение не считаются).
    private static bool SegmentsCross(PlanPoint a, PlanPoint b, PlanPoint c, PlanPoint d)
    {
        double Side(PlanPoint p, PlanPoint q, PlanPoint r) => ((q.XMm - p.XMm) * (r.YMm - p.YMm)) - ((q.YMm - p.YMm) * (r.XMm - p.XMm));
        double d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
        var eps = ToleranceMm * Math.Max(Length2(a, b), Length2(c, d));
        return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
    }

    private static double Length2(PlanPoint a, PlanPoint b) => Math.Sqrt(((b.XMm - a.XMm) * (b.XMm - a.XMm)) + ((b.YMm - a.YMm) * (b.YMm - a.YMm)));

    private static double DistanceToSegment(PlanPoint p, PlanPoint a, PlanPoint b)
    {
        double dx = b.XMm - a.XMm, dy = b.YMm - a.YMm;
        var len2 = (dx * dx) + (dy * dy);
        var t = len2 == 0 ? 0 : Math.Clamp((((p.XMm - a.XMm) * dx) + ((p.YMm - a.YMm) * dy)) / len2, 0, 1);
        double ex = a.XMm + (t * dx) - p.XMm, ey = a.YMm + (t * dy) - p.YMm;
        return Math.Sqrt((ex * ex) + (ey * ey));
    }
}
