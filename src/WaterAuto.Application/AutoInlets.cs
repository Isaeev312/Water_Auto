using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Точки ввода по умолчанию (design.autoInlets): помещению без ПТ_ВВОД ставится точка у ближайшей к насосной стены
/// (без насосной — к вводу в здание, без него — у стены, ближайшей к центру плана), с отступом внутрь помещения.
/// Это решение по трассировке, а не по норме: каждая поставленная точка — предупреждение с координатами, чтобы проектировщик
/// перенёс её на место стояка или узла управления.
/// </summary>
public static class AutoInlets
{
    /// <summary>Отступ точки от стены внутрь помещения, мм (чтобы точка не легла на контур).</summary>
    private const double InsetMm = 500;

    public static FloorPlan Apply(FloorPlan plan, List<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(issues);
        var target = plan.PumpRooms.Count > 0 ? plan.PumpRooms[0].Outline.Centroid
            : plan.BuildingInlets.Count > 0 ? plan.BuildingInlets[0]
            : Center(plan);
        var added = new List<Inlet>();
        foreach (var room in plan.Rooms.Where(r => !plan.Inlets.Any(i => r.Contour.Contains(i.Location))))
        {
            if (Place(room.Contour, target) is not { } at)
            {
                continue;
            }

            added.Add(new Inlet("AUTO." + room.Id, at, null));
            issues.Add(new Issue("design.autoInlet", IssueLevel.Warning,
                string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Помещение «{room.Name}»: точки ПТ_ВВОД нет — ввод принят у стены со стороны {(plan.PumpRooms.Count > 0 ? "насосной" : "ввода в здание или центра плана")} (X {at.XMm:0}, Y {at.YMm:0} мм). Перенесите точку на место стояка или узла управления на чертеже."),
                room.Id, Location: at));
        }

        return added.Count == 0 ? plan : plan with { Inlets = [.. plan.Inlets, .. added] };
    }

    private static PlanPoint Center(FloorPlan plan)
    {
        var all = plan.Rooms.SelectMany(r => r.Contour.Vertices).ToList();
        return all.Count == 0 ? new PlanPoint(0, 0) : new PlanPoint(all.Average(p => p.XMm), all.Average(p => p.YMm));
    }

    // Ближайшая к цели точка контура, сдвинутая внутрь к центру тяжести; если и так не внутри — центр тяжести, если он внутри.
    private static PlanPoint? Place(Polygon contour, PlanPoint target)
    {
        var best = contour.Edges().Select(e => Nearest(target, e.A, e.B)).MinBy(p => Dist(p, target));
        var c = contour.Centroid;
        var d = Dist(best, c);
        if (d > InsetMm)
        {
            var inset = new PlanPoint(best.XMm + ((c.XMm - best.XMm) * InsetMm / d), best.YMm + ((c.YMm - best.YMm) * InsetMm / d));
            if (contour.Contains(inset) && contour.DistanceTo(inset) > Polygon.ToleranceMm)
            {
                return inset;
            }
        }

        return contour.Contains(c) ? c : null;
    }

    private static PlanPoint Nearest(PlanPoint p, PlanPoint a, PlanPoint b)
    {
        var dx = b.XMm - a.XMm;
        var dy = b.YMm - a.YMm;
        var len2 = (dx * dx) + (dy * dy);
        var t = len2 <= 0 ? 0 : Math.Clamp((((p.XMm - a.XMm) * dx) + ((p.YMm - a.YMm) * dy)) / len2, 0, 1);
        return new PlanPoint(a.XMm + (t * dx), a.YMm + (t * dy));
    }

    private static double Dist(PlanPoint a, PlanPoint b) => Math.Sqrt(Math.Pow(a.XMm - b.XMm, 2) + Math.Pow(a.YMm - b.YMm, 2));
}
