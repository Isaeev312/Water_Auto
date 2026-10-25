using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Расстановка пожарных кранов: места кранов, проверочные точки и непокрытые точки.</summary>
public sealed record HydrantPlacementResult(IReadOnlyList<PlanPoint> Points, IReadOnlyList<PlanPoint> TestPoints, int Uncovered, IReadOnlyList<Issue> Issues);

/// <summary>
/// Пожарные краны у стен помещения так, чтобы каждая точка помещения орошалась заданным числом струй (п. 7.14 СН КР 40-04:2025):
/// проверочные точки — сетка по помещению вне зон запрета; кандидаты — у стен с шагом; струя достаёт точку, если она
/// не дальше радиуса действия и видна по прямой внутри помещения (без пересечения стен и зон). Выбор жадный: на каждом шаге
/// кран, закрывающий больше всего недостающих струй; краны ближе <see cref="RiserSeparationMm"/> считаются одним стояком
/// и рядом не ставятся (две струи — из двух разных стояков). Радиус действия задаётся проектом (В-22).
/// </summary>
public static class HydrantPlacement
{
    private const double GridMm = 2000;
    private const double CandidateStepMm = 1000;
    private const double WallOffsetMm = 300;
    private const double SampleMm = 250;

    /// <summary>Краны ближе этого расстояния считаются одним стояком: две струи в точку — от двух разных стояков (п. 7.14).</summary>
    public const double RiserSeparationMm = 3000;

    public static HydrantPlacementResult Place(FloorPlan plan, Room room, Length reach, int jets)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(room);
        var zones = plan.NoSprinklerZones.Where(z => z.Outline.RelationTo(room.Contour) != PolygonRelation.Disjoint).Select(z => z.Outline).ToList();
        bool Free(PlanPoint p) => room.Contour.Contains(p) && !zones.Any(z => z.Contains(p));
        bool Visible(PlanPoint a, PlanPoint b)
        {
            var d = Dist(a, b);
            var n = Math.Max(1, (int)Math.Ceiling(d / SampleMm));
            for (var k = 0; k <= n; k++)
            {
                var t = (double)k / n;
                if (!Free(new PlanPoint(a.XMm + ((b.XMm - a.XMm) * t), a.YMm + ((b.YMm - a.YMm) * t))))
                {
                    return false;
                }
            }

            return true;
        }

        var v = room.Contour.Vertices;
        double x0 = v.Min(p => p.XMm), x1 = v.Max(p => p.XMm), y0 = v.Min(p => p.YMm), y1 = v.Max(p => p.YMm);
        var tests = new List<PlanPoint>();
        for (var x = x0 + (GridMm / 2); x < x1; x += GridMm)
        {
            for (var y = y0 + (GridMm / 2); y < y1; y += GridMm)
            {
                var p = new PlanPoint(x, y);
                if (Free(p))
                {
                    tests.Add(p);
                }
            }
        }

        // Кандидаты: вдоль каждой стены внутри помещения на отступе от неё.
        var candidates = new List<PlanPoint>();
        foreach (var (a, b) in room.Contour.Edges())
        {
            var len = Dist(a, b);
            if (len < 1)
            {
                continue;
            }

            double ux = (b.XMm - a.XMm) / len, uy = (b.YMm - a.YMm) / len;
            for (var s = CandidateStepMm / 2; s < len; s += CandidateStepMm)
            {
                var basePoint = new PlanPoint(a.XMm + (ux * s), a.YMm + (uy * s));
                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    var p = new PlanPoint(basePoint.XMm - (uy * WallOffsetMm * sign), basePoint.YMm + (ux * WallOffsetMm * sign));
                    if (Free(p))
                    {
                        candidates.Add(p);
                        break;
                    }
                }
            }
        }

        var r = reach.Millimeters;
        var sees = candidates.Select(c => tests.Select((t, i) => (t, i)).Where(x => Dist(c, x.t) <= r && Visible(c, x.t)).Select(x => x.i).ToList()).ToList();
        var need = tests.Select(_ => jets).ToArray();
        var used = new HashSet<int>();
        var chosen = new List<PlanPoint>();
        while (need.Any(n => n > 0))
        {
            var best = -1;
            var bestGain = 0;
            for (var c = 0; c < candidates.Count; c++)
            {
                if (used.Contains(c) || chosen.Any(h => Dist(h, candidates[c]) < RiserSeparationMm))
                {
                    continue;
                }

                var gain = sees[c].Count(i => need[i] > 0);
                if (gain > bestGain)
                {
                    (best, bestGain) = (c, gain);
                }
            }

            if (best < 0)
            {
                break;
            }

            used.Add(best);
            chosen.Add(candidates[best]);
            foreach (var i in sees[best])
            {
                need[i] = Math.Max(0, need[i] - 1);
            }
        }

        var uncovered = need.Count(n => n > 0);
        var issues = new List<Issue>();
        if (uncovered > 0)
        {
            issues.Add(new Issue(
                "vpv.uncovered",
                IssueLevel.Warning,
                $"Помещение «{room.Name}»: {uncovered} проверочных точек не достаются {jets} струями при радиусе {reach} — нужны краны у колонн или больший радиус.",
                room.Id,
                "7.14",
                tests[Array.FindIndex(need, n => n > 0)]));
        }

        // Нумерация ПК по плану: сверху вниз, слева направо.
        var ordered = chosen.OrderByDescending(p => Math.Round(p.YMm / GridMm)).ThenBy(p => p.XMm).ToList();
        return new HydrantPlacementResult(ordered, tests, uncovered, issues);
    }

    private static double Dist(PlanPoint a, PlanPoint b) => Math.Sqrt(((a.XMm - b.XMm) * (a.XMm - b.XMm)) + ((a.YMm - b.YMm) * (a.YMm - b.YMm)));
}
