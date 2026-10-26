using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Отрезок трубы для проверки коллизий: концы в плане, мм, отметка оси, м, наружный диаметр, мм.</summary>
public sealed record PipeSegment3(PlanPoint A, PlanPoint B, double Z, double OuterMm);

/// <summary>
/// Коллизии труб на пересечениях в плане: оси должны расходиться по высоте не меньше полусуммы наружных диаметров
/// и зазора (между трубой и конструкциями — не менее 2 см, п. 6.7.1.23; тот же зазор принят между трубами).
/// </summary>
public static class PipeCollisions
{
    public static IReadOnlyList<Issue> Check(IReadOnlyList<PipeSegment3> first, IReadOnlyList<PipeSegment3> second, double gapMm, string what)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var issues = new List<Issue>();
        foreach (var a in first)
        {
            foreach (var b in second)
            {
                if (Cross(a.A, a.B, b.A, b.B) is not { } at)
                {
                    continue;
                }

                var need = ((a.OuterMm + b.OuterMm) / 2) + gapMm;
                var have = Math.Abs(a.Z - b.Z) * 1000;
                if (have < need)
                {
                    issues.Add(new Issue("pipe.collision", IssueLevel.Warning,
                        $"Коллизия: {what} — оси на {a.Z:+0.000} и {b.Z:+0.000}, по высоте {have:0} мм при нужных не менее {need:0} мм (наружные диаметры и зазор): измените отметку прокладки.",
                        null, "6.7.1.23", at));
                }
            }
        }

        // Одна коллизия на точку (соседние отрезки дают одно пересечение дважды).
        return [.. issues.GroupBy(i => (Math.Round(i.Location!.Value.XMm), Math.Round(i.Location.Value.YMm))).Select(g => g.First())];
    }

    /// <summary>Наибольшее нужное расхождение осей по высоте среди пересечений в плане, мм; 0 — пересечений нет.</summary>
    public static double MaxRequiredGap(IReadOnlyList<PipeSegment3> first, IReadOnlyList<PipeSegment3> second, double gapMm)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.SelectMany(a => second.Where(b => Cross(a.A, a.B, b.A, b.B) is not null).Select(b => ((a.OuterMm + b.OuterMm) / 2) + gapMm)).DefaultIfEmpty(0).Max();
    }

    private static PlanPoint? Cross(PlanPoint a, PlanPoint b, PlanPoint c, PlanPoint d)
    {
        double rx = b.XMm - a.XMm, ry = b.YMm - a.YMm, sx = d.XMm - c.XMm, sy = d.YMm - c.YMm;
        var den = (rx * sy) - (ry * sx);
        if (Math.Abs(den) < 1e-9)
        {
            return null;
        }

        var t = (((c.XMm - a.XMm) * sy) - ((c.YMm - a.YMm) * sx)) / den;
        var u = (((c.XMm - a.XMm) * ry) - ((c.YMm - a.YMm) * rx)) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? new PlanPoint(a.XMm + (rx * t), a.YMm + (ry * t)) : null;
    }
}
