using WaterAuto.Core;

namespace WaterAuto.Calc.Powder;

/// <summary>
/// Расстановка модулей порошкового пожаротушения на потолке равномерной сеткой по габаритам помещения: число модулей —
/// не меньше расчётного N (прил. И), ячейка — не больше стороны квадрата защищаемой одним модулем площади √Sн, чтобы зона
/// распыла (квадрат по документации) накрывала свою ячейку. Модули — в центрах ячеек внутри контура. Точная расстановка
/// с учётом диаграмм распыла, затенений и оборудования — по ТД на модуль (п. 10.2.13), проектировщик.
/// </summary>
public static class PowderLayout
{
    public static IReadOnlyList<PlanPoint> Place(Polygon outline, int modules, double perModuleM2)
    {
        ArgumentNullException.ThrowIfNull(outline);
        var xs = outline.Vertices.Select(v => v.XMm).ToList();
        var ys = outline.Vertices.Select(v => v.YMm).ToList();
        double w = xs.Max() - xs.Min(), h = ys.Max() - ys.Min();
        var side = Math.Sqrt(Math.Max(perModuleM2, 1e-9)) * 1000;
        var minX = Math.Max(1, (int)Math.Ceiling((w / side) - 1e-9));
        var minY = Math.Max(1, (int)Math.Ceiling((h / side) - 1e-9));

        // Наименьшая сетка nx × ny ≥ N при ячейке не больше стороны квадрата; при равенстве — ячейки ближе к квадрату.
        (int Nx, int Ny)? best = null;
        for (var nx = minX; nx <= Math.Max(minX, modules) + minX; nx++)
        {
            var ny = Math.Max(minY, (int)Math.Ceiling((double)modules / nx));
            var better = best is not { } b || nx * ny < b.Nx * b.Ny
                || (nx * ny == b.Nx * b.Ny && Math.Abs(Math.Log(w / nx / (h / ny))) < Math.Abs(Math.Log(w / b.Nx / (h / b.Ny))));
            if (better) { best = (nx, ny); }
        }

        var (cx, cy) = best!.Value;
        var points = new List<PlanPoint>();
        for (var j = 0; j < cy; j++)
        {
            for (var i = 0; i < cx; i++)
            {
                var p = new PlanPoint(xs.Min() + (w * (i + 0.5) / cx), ys.Min() + (h * (j + 0.5) / cy));
                if (outline.Contains(p)) { points.Add(p); }
            }
        }

        return points;
    }
}
