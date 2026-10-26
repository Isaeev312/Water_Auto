using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>
/// Правила прокладки обвязки (рекомендации литературы, значения — из данных): от трубы до стены, высота кронштейнов,
/// зазор между ярусами в плане.
/// </summary>
public sealed record PipingRules(double WallOffsetMm, double BracketHeightMm, double LevelGapMm);

/// <summary>
/// Трубопровод для трассировки: From/To — точка на стене (высота null — вход через стену на высоте кронштейнов) или патрубок
/// оборудования с высотой оси, мм; Level — ярус (0 — ближе к стене).
/// </summary>
public sealed record PipeRunRequest(string Name, int Dn, double OuterMm, PlanPoint From, double? FromHeightMm, PlanPoint To, double? ToHeightMm, int Level);

/// <summary>Трасса: ломаная в плане, длины горизонтальных и вертикальных участков, мм, число отводов.</summary>
public sealed record PipeRun(string Name, int Dn, IReadOnlyList<PlanPoint> Path, double HorizontalMm, double VerticalMm, int Elbows);

public sealed record PipingResult(IReadOnlyList<PipeRun> Runs, IReadOnlyList<Issue> Issues);

/// <summary>
/// Трассировка обвязки в прямоугольной насосной: трубы — на кронштейнах вдоль стен на заданной высоте, с отступом от стены
/// (ось — отступ + половина наружного диаметра; ярус 1 — дальше на диаметр и зазор). Точка на стене выводится перпендикулярно
/// на линию яруса, к патрубку оборудования — от ближайшей стены перпендикуляром и опуском. Между ними — по периметру,
/// в более короткую сторону. Между двумя единицами оборудования — напрямую, Г-образно, на высоте кронштейнов. Отводы: повороты в плане и по два на каждый опуск (подъём).
/// </summary>
public static class PumpRoomPiping
{
    private const double OnWall = 500;

    public static PipingResult Route(Polygon room, IReadOnlyList<PipeRunRequest> requests, PipingRules rules)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(rules);
        var v = room.Vertices;
        var (x0, y0, x1, y1) = (v.Min(p => p.XMm), v.Min(p => p.YMm), v.Max(p => p.XMm), v.Max(p => p.YMm));
        var runs = new List<PipeRun>();
        var issues = new List<Issue>();
        var outerOfLevel0 = requests.Where(r => r.Level == 0).Select(r => r.OuterMm).DefaultIfEmpty(0).Max();
        foreach (var r in requests)
        {
            // Ось яруса 0 — отступ + половина диаметра; ярус 1 — за трубой яруса 0 через зазор.
            var d = r.Level == 0
                ? rules.WallOffsetMm + (r.OuterMm / 2)
                : rules.WallOffsetMm + outerOfLevel0 + rules.LevelGapMm + (r.OuterMm / 2);

            var (ix0, iy0, ix1, iy1) = (x0 + d, y0 + d, x1 - d, y1 - d);
            PlanPoint? Project(PlanPoint p, bool wall)
            {
                if (wall)
                {
                    // Ближайшая стена; точка — на линии яруса напротив.
                    var dist = new[] { p.XMm - x0, x1 - p.XMm, p.YMm - y0, y1 - p.YMm };
                    if (dist.Min() > OnWall)
                    {
                        issues.Add(new Issue("pumpRoom.pointNotOnWall", IssueLevel.Error, $"Точка «{r.Name}» не на стене насосной (до ближайшей стены {dist.Min():0} мм).", null, null, p));
                        return null;
                    }

                    var k = Array.IndexOf(dist, dist.Min());
                    return k switch
                    {
                        0 => new(ix0, Math.Clamp(p.YMm, iy0, iy1)),
                        1 => new(ix1, Math.Clamp(p.YMm, iy0, iy1)),
                        2 => new(Math.Clamp(p.XMm, ix0, ix1), iy0),
                        _ => new(Math.Clamp(p.XMm, ix0, ix1), iy1),
                    };
                }

                // Патрубок внутри помещения — ближайшая точка на линии яруса.
                var c = new[] { (p.XMm - ix0, new PlanPoint(ix0, p.YMm)), (ix1 - p.XMm, new PlanPoint(ix1, p.YMm)), (p.YMm - iy0, new PlanPoint(p.XMm, iy0)), (iy1 - p.YMm, new PlanPoint(p.XMm, iy1)) };
                var best = c.MinBy(x => Math.Abs(x.Item1)).Item2;
                return new(Math.Clamp(best.XMm, ix0, ix1), Math.Clamp(best.YMm, iy0, iy1));
            }

            var fromWall = r.FromHeightMm is null;
            var toWall = r.ToHeightMm is null;
            List<PlanPoint> path;
            if (!fromWall && !toWall)
            {
                // Между двумя единицами оборудования — напрямую над проходом: Г-образно, сначала вдоль X.
                path = [r.From, new PlanPoint(r.To.XMm, r.From.YMm), r.To];
            }
            else
            {
                if (Project(r.From, fromWall) is not { } a || Project(r.To, toWall) is not { } b)
                {
                    continue;
                }

                path = [r.From, a, .. Perimeter(a, b, ix0, iy0, ix1, iy1), b, r.To];
            }

            var clean = new List<PlanPoint>();
            foreach (var p in path)
            {
                if (clean.Count == 0 || Dist(clean[^1], p) > 1)
                {
                    clean.Add(p);
                }
            }

            // Убираем промежуточные точки на одной прямой.
            for (var k = clean.Count - 2; k >= 1; k--)
            {
                var (p0, p1, p2) = (clean[k - 1], clean[k], clean[k + 1]);
                if (Math.Abs(((p1.XMm - p0.XMm) * (p2.YMm - p1.YMm)) - ((p1.YMm - p0.YMm) * (p2.XMm - p1.XMm))) < 1e-6)
                {
                    clean.RemoveAt(k);
                }
            }

            var horizontal = clean.Zip(clean.Skip(1), Dist).Sum();
            var vertical = (fromWall ? 0 : Math.Abs(rules.BracketHeightMm - r.FromHeightMm!.Value)) + (toWall ? 0 : Math.Abs(rules.BracketHeightMm - r.ToHeightMm!.Value));
            var elbows = Math.Max(0, clean.Count - 2) + (fromWall ? 0 : 2) + (toWall ? 0 : 2);
            runs.Add(new PipeRun(r.Name, r.Dn, clean, horizontal, vertical, elbows));
        }

        return new PipingResult(runs, issues);
    }

    // Углы прямоугольника яруса между точками a и b по периметру, в более короткую сторону.
    private static List<PlanPoint> Perimeter(PlanPoint a, PlanPoint b, double x0, double y0, double x1, double y1)
    {
        var (w, h) = (x1 - x0, y1 - y0);
        var length = 2 * (w + h);
        double T(PlanPoint p) =>
            Math.Abs(p.YMm - y0) < 1e-6 ? p.XMm - x0
            : Math.Abs(p.XMm - x1) < 1e-6 ? w + (p.YMm - y0)
            : Math.Abs(p.YMm - y1) < 1e-6 ? w + h + (x1 - p.XMm)
            : 2 * w + h + (y1 - p.YMm);
        (double T, PlanPoint P)[] corners = [(0.0, new PlanPoint(x0, y0)), (w, new PlanPoint(x1, y0)), (w + h, new PlanPoint(x1, y1)), ((2 * w) + h, new PlanPoint(x0, y1))];
        var (ta, tb) = (T(a), T(b));
        var forward = ((tb - ta) % length + length) % length;
        var list = new List<PlanPoint>();
        if (forward <= length - forward)
        {
            foreach (var c in corners.Select(c => (D: ((c.T - ta) % length + length) % length, c.P)).Where(c => c.D > 1e-6 && c.D < forward - 1e-6).OrderBy(c => c.D))
            {
                list.Add(c.P);
            }
        }
        else
        {
            foreach (var c in corners.Select(c => (D: ((ta - c.T) % length + length) % length, c.P)).Where(c => c.D > 1e-6 && c.D < length - forward - 1e-6).OrderBy(c => c.D))
            {
                list.Add(c.P);
            }
        }

        return list;
    }

    private static double Dist(PlanPoint a, PlanPoint b) => Math.Sqrt(Math.Pow(b.XMm - a.XMm, 2) + Math.Pow(b.YMm - a.YMm, 2));
}
