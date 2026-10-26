using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Строительные элементы насосной в плане, мм: приямок (п. 6.10.23) и фундамент установки (п. 6.10.26).</summary>
public sealed record PumpRoomFeatureSet((double X0, double Y0, double X1, double Y1)? Pit, (double X0, double Y0, double X1, double Y1)? Foundation);

/// <summary>
/// Приямок — квадрат в углу помещения, в котором ближе 0,5 м нет оборудования и двери (из свободных — дальний от двери);
/// фундамент — по раме установки с отступом.
/// </summary>
public static class PumpRoomFeatures
{
    private const double Clear = 500;

    public static PumpRoomFeatureSet Build(Polygon room, IReadOnlyList<PlacedItem> items, PlanPoint? door, double pitMm, double foundationMarginMm)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(items);
        var v = room.Vertices;
        var (x0, y0, x1, y1) = (v.Min(p => p.XMm), v.Min(p => p.YMm), v.Max(p => p.XMm), v.Max(p => p.YMm));
        var corners = new[] { (x0, y0, x0 + pitMm, y0 + pitMm), (x1 - pitMm, y0, x1, y0 + pitMm), (x1 - pitMm, y1 - pitMm, x1, y1), (x0, y1 - pitMm, x0 + pitMm, y1) };
        bool Free((double A0, double B0, double A1, double B1) r) =>
            items.All(i => i.X1 + Clear <= r.A0 || i.X0 - Clear >= r.A1 || i.Y1 + Clear <= r.B0 || i.Y0 - Clear >= r.B1)
            && (door is not { } d || d.XMm < r.A0 - Clear - 500 || d.XMm > r.A1 + Clear + 500 || d.YMm < r.B0 - Clear - 500 || d.YMm > r.B1 + Clear + 500);
        double FromDoor((double A0, double B0, double A1, double B1) r) => door is { } d ? Math.Abs(((r.A0 + r.A1) / 2) - d.XMm) + Math.Abs(((r.B0 + r.B1) / 2) - d.YMm) : 0;
        var pit = corners.Where(Free).OrderByDescending(FromDoor).Select(c => ((double, double, double, double)?)c).FirstOrDefault();
        var set = items.FirstOrDefault(i => i.Kind == StationItemKind.PumpSet);
        var foundation = set is null ? ((double, double, double, double)?)null : (set.X0 - foundationMarginMm, set.Y0 - foundationMarginMm, set.X1 + foundationMarginMm, set.Y1 + foundationMarginMm);
        return new PumpRoomFeatureSet(pit, foundation);
    }
}
