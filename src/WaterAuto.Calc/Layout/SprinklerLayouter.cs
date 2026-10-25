using WaterAuto.Core;

namespace WaterAuto.Calc.Layout;

/// <summary>Направление разрезов, которыми помещение делится на прямоугольные зоны.</summary>
public enum CutDirection
{
    Vertical,
    Horizontal,
}

internal sealed record RoomLayout(IReadOnlyList<PlacedSprinkler> Sprinklers, IReadOnlyList<LayoutZone> Zones, IReadOnlyList<Issue> Issues);

/// <summary>
/// Раскладка спринклерных оросителей по плану (этап 2, ADR 0004).
/// Помещение поворачивается вдоль своих стен и делится разрезами на прямоугольные зоны. Линии поперёк разрезов
/// общие для всего помещения, поэтому ряды соседних зон продолжают друг друга; линии вдоль разрезов — свои у каждой зоны.
/// Пределы — из <see cref="LayoutRules"/>: у стены — 6.2.21, у разреза — полшага (тогда шаг через разрез не больше нормы).
/// Оба направления разрезов сравниваются, берётся вариант с меньшим числом оросителей.
/// </summary>
public static class SprinklerLayouter
{
    private const double Tol = Polygon.ToleranceMm;

    public static SprinklerLayout Layout(FloorPlan plan, LayoutRules rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);
        var sprinklers = new List<PlacedSprinkler>();
        var zones = new List<LayoutZone>();
        var issues = new List<Issue>();
        foreach (var room in plan.Rooms)
        {
            var vertical = LayoutRoom(room, plan, rules, CutDirection.Vertical);
            var horizontal = LayoutRoom(room, plan, rules, CutDirection.Horizontal);
            var best = Better(horizontal, vertical) ? horizontal : vertical;
            sprinklers.AddRange(best.Sprinklers);
            zones.AddRange(best.Zones);
            issues.AddRange(best.Issues);
        }

        issues.AddRange(ObstacleIssues(plan, rules));
        return new SprinklerLayout(sprinklers, zones, issues);
    }

    internal static RoomLayout LayoutRoom(Room room, FloorPlan plan, LayoutRules rules, CutDirection cut)
    {
        var issues = new List<Issue>();
        var frame = Frame.For(room.Contour, cut == CutDirection.Horizontal);
        var contour = frame.ToLocal(room.Contour);
        if (NonOrthogonalEdge(contour) is { } bad)
        {
            issues.Add(new Issue(
                "layout.nonOrthogonal",
                IssueLevel.Error,
                $"Помещение «{room.Name}»: стена не параллельна и не перпендикулярна остальным — раскладка выполняется только для прямоугольных контуров (стены под углом — позже).",
                room.Id,
                Location: frame.ToWorld(bad)));
            return new RoomLayout([], [], issues);
        }

        // Вырезы раскладки: зоны без оросителей и балки выше предела 6.2.10 — их грани работают как стены (В-17).
        // Для трассы балки не препятствие: трубы проходят под ними.
        var holes = new List<List<PlanPoint>>();
        var beamHoles = new List<List<PlanPoint>>();
        var skippedZones = new List<Polygon>();
        var cutouts = plan.NoSprinklerZones.Select(z => (z.Id, z.Outline, What: "Зона без оросителей"))
            .Concat(plan.Beams.Where(b => b.Height.Meters > rules.BeamHeightLimit.Meters + 1e-9).Select(b => (b.Id, b.Outline, What: "Балка")));
        foreach (var (id, outline, what) in cutouts.Where(c => c.Outline.RelationTo(room.Contour) != PolygonRelation.Disjoint))
        {
            var local = frame.ToLocal(outline);
            if (NonOrthogonalEdge(local) is { } zoneBad)
            {
                issues.Add(new Issue(
                    "layout.nonOrthogonal",
                    IssueLevel.Error,
                    $"{what} не прямоугольная относительно стен помещения: оросители внутри неё удалены, расстояния у её границ не проверены.",
                    id,
                    Location: frame.ToWorld(zoneBad)));
                skippedZones.Add(outline);
                continue;
            }

            holes.Add(local);
            if (what == "Балка")
            {
                beamHoles.Add(local);
            }
        }

        var snap = new Snapper(contour.Concat(holes.SelectMany(h => h)));
        contour = snap.Apply(contour);
        holes = holes.Select(snap.Apply).ToList();
        var beams = beamHoles.Select(b => Box.Of(snap.Apply(b))).ToList();
        var boundary = Segments(contour).Concat(holes.SelectMany(Segments)).Where(e => Len(e) > Tol).ToList();
        var rects = Decompose(contour, holes);

        var s = rules.MaxSpacing.Millimeters;
        var m = rules.MinSpacing.Millimeters;
        var wall = rules.MaxWallDistance.Millimeters;
        var columns = plan.Columns
            .Where(c => c.Outline.RelationTo(room.Contour) != PolygonRelation.Disjoint)
            .Select(c => (Column: c, Box: Box.Of(frame.ToLocal(c.Outline))))
            .Where(c => rects.Any(r => r.Overlaps(c.Box)))
            .ToList();
        PlanPoint Center(Rect r) => frame.ToWorld(new PlanPoint((r.X0 + r.X1) / 2, (r.Y0 + r.Y1) / 2));
        var beamIssues = new List<Issue>();
        AxisSpan YBeamSpan(Rect r)
        {
            bool Overlaps(Box b) => Math.Min(r.X1, b.X1) - Math.Max(r.X0, b.X0) > Tol;
            return BeamFaces(
                YSpan(r, wall),
                b => Math.Abs(b.Y1 - r.Y0) <= Tol && Overlaps(b) ? b.Y1 - b.Y0 : null,
                b => Math.Abs(b.Y0 - r.Y1) <= Tol && Overlaps(b) ? b.Y1 - b.Y0 : null,
                beams,
                s,
                beamIssues,
                Center(r));
        }

        // Линии вдоль разрезов (X) — свои у каждой зоны. Зона, в которой не помещается ни одна линия (ниша, ступенька
        // стены), своих оросителей не получает: её дальнюю сторону обслуживают соседи, их предел у общей стороны уменьшается.
        var xSpans = rects.Select(r => XSpan(r, boundary, wall, s, m, beams, issues)).ToList();
        var absorbed = rects.Select((r, i) => LineSolver.Solve([xSpans[i]], s, m, []) is null).ToArray();
        for (var i = 0; i < rects.Count; i++)
        {
            if (!absorbed[i])
            {
                continue;
            }

            var r = rects[i];
            var width = r.X1 - r.X0;
            var served = false;
            for (var j = 0; j < rects.Count; j++)
            {
                if (absorbed[j] || !rects[j].SharesYRange(r))
                {
                    continue;
                }

                if (Math.Abs(rects[j].X1 - r.X0) <= Tol)
                {
                    // Сосед слева обслуживает зону до её правой стороны.
                    xSpans[j] = xSpans[j] with { EndLimitHi = Math.Min(xSpans[j].EndLimitHi, Across(Share(boundary, r.X1, r), width, wall, s)) };
                    served = true;
                }
                else if (Math.Abs(rects[j].X0 - r.X1) <= Tol)
                {
                    xSpans[j] = xSpans[j] with { EndLimitLo = Math.Min(xSpans[j].EndLimitLo, Across(Share(boundary, r.X0, r), width, wall, s)) };
                    served = true;
                }
            }

            if (!served)
            {
                issues.Add(Sliver(room, r, Center(r)));
            }
        }

        // Линии поперёк разрезов (Y) — общие; концы отрезков — всегда стены. Зона тоньше, чем нужно для линии, пропускается.
        var active = new List<int>();
        for (var i = 0; i < rects.Count; i++)
        {
            if (absorbed[i])
            {
                continue;
            }

            if (LineSolver.Solve([YBeamSpan(rects[i])], s, m, []) is null)
            {
                issues.Add(Sliver(room, rects[i], Center(rects[i])));
                continue;
            }

            active.Add(i);
        }

        var ySpans = active.Select(i => YBeamSpan(rects[i])).Distinct().ToList();
        var ys = ySpans.Count == 0 ? [] : SolveAvoiding(ySpans, s, m, columns.Select(c => (c.Box.Y0 - Tol, c.Box.Y1 + Tol)).ToList());
        if (ys is null)
        {
            issues.Add(Infeasible(room, Center(rects[active[0]])));
            return new RoomLayout([], [], issues);
        }

        issues.AddRange(beamIssues.DistinctBy(x => x.Location));
        var placed = new List<(PlanPoint Local, Rect Rect)>();
        var zones = new List<Rect>();
        foreach (var i in active)
        {
            var r = rects[i];
            var rows = ys.Where(y => y > r.Y0 && y < r.Y1).ToList();

            // Колонны, на ряды которых попадают линии зоны, обходятся сдвигом линий X.
            var blocking = columns
                .Where(c => r.Overlaps(c.Box) && rows.Any(y => y >= c.Box.Y0 - Tol && y <= c.Box.Y1 + Tol))
                .Select(c => (c.Box.X0 - Tol, c.Box.X1 + Tol))
                .ToList();
            var xs = SolveAvoiding([xSpans[i]], s, m, blocking);
            if (xs is null)
            {
                issues.Add(Infeasible(room, Center(r)));
                continue;
            }

            zones.Add(r);
            foreach (var y in rows)
            {
                foreach (var x in xs)
                {
                    placed.Add((new PlanPoint(x, y), r));
                }
            }
        }

        var kept = new List<(PlanPoint Local, Rect Rect, PlanPoint World)>();
        foreach (var (local, rect) in placed)
        {
            var world = frame.ToWorld(local);
            var hit = columns.FirstOrDefault(c => c.Column.Outline.Contains(world));
            if (hit.Column is not null)
            {
                issues.Add(new Issue(
                    "layout.columnConflict",
                    IssueLevel.Error,
                    "Ороситель попадает в колонну, а сдвиг линий в пределах шага и расстояния до стен невозможен — ороситель не поставлен, нужна ручная правка.",
                    hit.Column.Id,
                    "6.2.21",
                    world));
                continue;
            }

            if (!skippedZones.Any(z => z.Contains(world)))
            {
                kept.Add((local, rect, world));
            }
        }

        var ordered = kept.OrderBy(x => frame.Unswapped(x.Local).YMm).ThenBy(x => frame.Unswapped(x.Local).XMm).ToList();
        var sprinklers = ordered.Select((x, k) => new PlacedSprinkler($"{room.Id}.S{k + 1}", room.Id, x.World)).ToList();
        var outZones = zones
            .Select(z => new LayoutZone(room.Id, frame.ToWorld(z.Polygon()), ordered.Count(x => x.Rect == z), frame.RowAngle))
            .ToList();
        return new RoomLayout(sprinklers, outZones, issues);
    }

    // Меньше ошибок, затем меньше оросителей, затем меньше зон; при равенстве — вертикальные разрезы.
    private static bool Better(RoomLayout a, RoomLayout b)
    {
        int Errors(RoomLayout x) => x.Issues.Count(i => i.Level == IssueLevel.Error);
        if (Errors(a) != Errors(b))
        {
            return Errors(a) < Errors(b);
        }

        return a.Sprinklers.Count != b.Sprinklers.Count ? a.Sprinklers.Count < b.Sprinklers.Count : a.Zones.Count < b.Zones.Count;
    }

    // Сначала с обходом колонн; если обход требует больше линий или невозможен — без обхода.
    private static IReadOnlyList<double>? SolveAvoiding(IReadOnlyList<AxisSpan> spans, double s, double m, List<(double, double)> forbidden)
    {
        var plain = LineSolver.Solve(spans, s, m, []);
        if (forbidden.Count == 0 || plain is null)
        {
            return plain;
        }

        var avoiding = LineSolver.Solve(spans, s, m, forbidden);
        return avoiding is not null && avoiding.Count <= plain.Count ? avoiding : plain;
    }

    private static AxisSpan YSpan(Rect r, double wall) => new(r.Y0, r.Y1, wall, wall, LineSolver.StepMm, LineSolver.StepMm);

    // Сторона зоны на грани балки: до грани не дальше предела 6.2.21 и шаг через балку не больше нормы —
    // предел min(предел у стены, (s − ширина балки) / 2), так выполняются оба прочтения В-17.
    private static AxisSpan BeamFaces(AxisSpan span, Func<Box, double?> loWidth, Func<Box, double?> hiWidth, List<Box> beams, double s, List<Issue> issues, PlanPoint at)
    {
        double Limit(double current, double? width)
        {
            if (width is not { } w)
            {
                return current;
            }

            var across = (s - w) / 2;
            if (across < LineSolver.StepMm)
            {
                issues.Add(new Issue("layout.beamWide", IssueLevel.Warning, $"Балка шириной {w:0} мм не меньше наибольшего шага: шаг через балку по норме не выдержать — у граней принят предел до стены.", Clause: "6.2.10", Location: at));
                return current;
            }

            return Math.Min(current, across);
        }

        var lo = beams.Select(loWidth).FirstOrDefault(w => w is not null);
        var hi = beams.Select(hiWidth).FirstOrDefault(w => w is not null);
        return span with { EndLimitLo = Limit(span.EndLimitLo, lo), EndLimitHi = Limit(span.EndLimitHi, hi) };
    }

    // Сторона зоны: стена (доля длины на границе ≈ 1) — предел 6.2.21; разрез (≈ 0) — полшага;
    // частично стена — меньшее из двух. У разреза линия не ближе половины наименьшего шага: через разрез не ближе нормы.
    private static AxisSpan XSpan(Rect r, List<(PlanPoint A, PlanPoint B)> boundary, double wall, double s, double m, List<Box> beams, List<Issue> issues)
    {
        (double Limit, double MinOffset) Side(double x)
        {
            var share = Share(boundary, x, r);
            return share >= 1 - 1e-6 ? (wall, LineSolver.StepMm)
                : share <= 1e-6 ? (s / 2, m / 2)
                : (Math.Min(wall, s / 2), m / 2);
        }

        var (loLimit, loMin) = Side(r.X0);
        var (hiLimit, hiMin) = Side(r.X1);
        bool Overlaps(Box b) => Math.Min(r.Y1, b.Y1) - Math.Max(r.Y0, b.Y0) > Tol;
        return BeamFaces(
            new AxisSpan(r.X0, r.X1, loLimit, hiLimit, loMin, hiMin),
            b => Math.Abs(b.X1 - r.X0) <= Tol && Overlaps(b) ? b.X1 - b.X0 : null,
            b => Math.Abs(b.X0 - r.X1) <= Tol && Overlaps(b) ? b.X1 - b.X0 : null,
            beams,
            s,
            issues,
            new PlanPoint((r.X0 + r.X1) / 2, (r.Y0 + r.Y1) / 2));
    }

    // Предел соседа у стороны узкой зоны шириной width, если дальняя сторона зоны — стена (до неё не дальше предела 6.2.21),
    // разрез (через зону шаг не больше нормы — поровну с соседом за ней) или частично стена (меньшее из двух).
    private static double Across(double farShare, double width, double wall, double s) =>
        farShare >= 1 - 1e-6 ? wall - width
        : farShare <= 1e-6 ? (s - width) / 2
        : Math.Min(wall - width, (s - width) / 2);

    private static double Share(List<(PlanPoint A, PlanPoint B)> boundary, double x, Rect r) => Coverage(boundary, x, r.Y0, r.Y1) / (r.Y1 - r.Y0);

    private static IEnumerable<Issue> ObstacleIssues(FloorPlan plan, LayoutRules rules)
    {
        foreach (var beam in plan.Beams.Where(b => b.Height.Meters > rules.BeamHeightLimit.Meters + 1e-9))
        {
            yield return new Issue(
                "layout.beamFields",
                IssueLevel.Info,
                $"Балка высотой {beam.Height} больше {rules.BeamHeightLimit}: оросители размещены в полях между балками: до граней балки не дальше предела п. 6.2.21 и шаг через балку не больше нормы (выполнены оба прочтения В-17).",
                beam.Id,
                "6.2.10",
                beam.Outline.Centroid);
        }

        foreach (var duct in plan.Ducts)
        {
            var longest = duct.Outline.Edges().Max(e => Dist(e.A, e.B));
            var width = longest > 0 ? duct.Outline.AreaMm2 / longest : 0;
            if (width > rules.DuctMinWidth.Millimeters + 1e-6)
            {
                yield return new Issue(
                    "layout.duct",
                    IssueLevel.Warning,
                    $"Короб шириной {Length.FromMillimeters(width)} больше {rules.DuctMinWidth}: если он препятствует орошению и расположен на высоте по п. 6.2.14, под ним нужны дополнительные оросители. Автоматически не проверяется.",
                    duct.Id,
                    "6.2.14",
                    duct.Outline.Centroid);
            }
        }
    }

    private static Issue Sliver(Room room, Rect r, PlanPoint at) => new(
        "layout.sliver",
        IssueLevel.Warning,
        $"Помещение «{room.Name}»: участок {r.X1 - r.X0:0} × {r.Y1 - r.Y0:0} мм слишком узок для оросителя и не обслуживается соседними зонами — проверьте чертёж.",
        room.Id,
        "6.2.21",
        at);

    private static Issue Infeasible(Room room, PlanPoint at) => new(
        "layout.infeasible",
        IssueLevel.Error,
        $"Помещение «{room.Name}»: в зоне нельзя расставить оросители с шагом и расстояниями до стен по норме — нужна ручная раскладка.",
        room.Id,
        "6.2.21",
        at);

    private static PlanPoint? NonOrthogonalEdge(List<PlanPoint> poly)
    {
        foreach (var (a, b) in Segments(poly))
        {
            if (Math.Abs(a.XMm - b.XMm) > Tol && Math.Abs(a.YMm - b.YMm) > Tol)
            {
                return new PlanPoint((a.XMm + b.XMm) / 2, (a.YMm + b.YMm) / 2);
            }
        }

        return null;
    }

    // Деление на прямоугольники вертикальными разрезами по всем X вершин; соседние полосы с одинаковым интервалом сливаются.
    private static List<Rect> Decompose(List<PlanPoint> contour, List<List<PlanPoint>> holes)
    {
        var xs = contour.Concat(holes.SelectMany(h => h)).Select(p => p.XMm).Distinct().Order().ToList();
        var rects = new List<Rect>();
        var open = new Dictionary<(double, double), double>();
        for (var k = 0; k + 1 < xs.Count; k++)
        {
            var mid = (xs[k] + xs[k + 1]) / 2;
            var intervals = Subtract(Inside(contour, mid), holes.SelectMany(h => Inside(h, mid)).ToList());
            foreach (var key in open.Keys.Where(key => !intervals.Contains(key)).ToList())
            {
                rects.Add(new Rect(open[key], xs[k], key.Item1, key.Item2));
                open.Remove(key);
            }

            foreach (var iv in intervals.Where(iv => !open.ContainsKey(iv)))
            {
                open[iv] = xs[k];
            }
        }

        rects.AddRange(open.Select(o => new Rect(o.Value, xs[^1], o.Key.Item1, o.Key.Item2)));
        return rects.OrderBy(r => r.X0).ThenBy(r => r.Y0).ToList();
    }

    // Интервалы Y внутри многоугольника на вертикали x (чётно-нечётное правило по горизонтальным сторонам).
    private static List<(double, double)> Inside(List<PlanPoint> poly, double x)
    {
        var ys = Segments(poly)
            .Where(e => Math.Abs(e.A.YMm - e.B.YMm) <= Tol && Math.Min(e.A.XMm, e.B.XMm) < x && Math.Max(e.A.XMm, e.B.XMm) > x)
            .Select(e => e.A.YMm)
            .Order()
            .ToList();
        var result = new List<(double, double)>();
        for (var i = 0; i + 1 < ys.Count; i += 2)
        {
            result.Add((ys[i], ys[i + 1]));
        }

        return result;
    }

    private static List<(double, double)> Subtract(List<(double, double)> from, List<(double, double)> holes)
    {
        var result = from;
        foreach (var (h0, h1) in holes)
        {
            result = result.SelectMany(iv =>
            {
                var parts = new List<(double, double)>();
                if (h0 > iv.Item1 + Tol)
                {
                    parts.Add((iv.Item1, Math.Min(iv.Item2, h0)));
                }

                if (h1 < iv.Item2 - Tol)
                {
                    parts.Add((Math.Max(iv.Item1, h1), iv.Item2));
                }

                return h1 <= iv.Item1 || h0 >= iv.Item2 ? [iv] : parts;
            }).ToList();
        }

        return result.Where(iv => iv.Item2 - iv.Item1 > Tol).ToList();
    }

    // Длина отрезка x = const, y ∈ [y0, y1], лежащая на вертикальных сторонах границы (наложения сторон — один раз).
    private static double Coverage(List<(PlanPoint A, PlanPoint B)> boundary, double x, double y0, double y1)
    {
        var parts = boundary
            .Where(e => Math.Abs(e.A.XMm - x) <= Tol && Math.Abs(e.B.XMm - x) <= Tol)
            .Select(e => (Lo: Math.Max(y0, Math.Min(e.A.YMm, e.B.YMm)), Hi: Math.Min(y1, Math.Max(e.A.YMm, e.B.YMm))))
            .Where(p => p.Hi > p.Lo)
            .OrderBy(p => p.Lo);
        double total = 0, reach = double.NegativeInfinity;
        foreach (var (lo, hi) in parts)
        {
            total += Math.Max(0, hi - Math.Max(lo, reach));
            reach = Math.Max(reach, hi);
        }

        return total;
    }

    private static IEnumerable<(PlanPoint A, PlanPoint B)> Segments(List<PlanPoint> poly) =>
        poly.Select((p, i) => (p, poly[(i + 1) % poly.Count]));

    private static double Len((PlanPoint A, PlanPoint B) e) => Dist(e.A, e.B);

    private static double Dist(PlanPoint a, PlanPoint b) => Math.Sqrt(((a.XMm - b.XMm) * (a.XMm - b.XMm)) + ((a.YMm - b.YMm) * (a.YMm - b.YMm)));

    private sealed record Rect(double X0, double X1, double Y0, double Y1)
    {
        public bool Overlaps(Box b) => b.X1 > X0 && b.X0 < X1 && b.Y1 > Y0 && b.Y0 < Y1;

        public bool SharesYRange(Rect o) => Math.Min(Y1, o.Y1) - Math.Max(Y0, o.Y0) > Tol;

        public List<PlanPoint> Polygon() => [new(X0, Y0), new(X1, Y0), new(X1, Y1), new(X0, Y1)];
    }

    private readonly record struct Box(double X0, double X1, double Y0, double Y1)
    {
        public static Box Of(List<PlanPoint> poly) =>
            new(poly.Min(p => p.XMm), poly.Max(p => p.XMm), poly.Min(p => p.YMm), poly.Max(p => p.YMm));
    }

    // Координаты, отличающиеся меньше допуска, сводятся к одной — стены чертежа становятся строго параллельными.
    private sealed class Snapper
    {
        private readonly List<double> _xs;
        private readonly List<double> _ys;

        public Snapper(IEnumerable<PlanPoint> points)
        {
            var all = points.ToList();
            _xs = Cluster(all.Select(p => p.XMm));
            _ys = Cluster(all.Select(p => p.YMm));
        }

        public List<PlanPoint> Apply(List<PlanPoint> poly) => poly.Select(p => new PlanPoint(Nearest(_xs, p.XMm), Nearest(_ys, p.YMm))).ToList();

        private static List<double> Cluster(IEnumerable<double> values)
        {
            var result = new List<double>();
            foreach (var v in values.Order())
            {
                if (result.Count == 0 || v - result[^1] > Tol)
                {
                    result.Add(v);
                }
            }

            return result;
        }

        private static double Nearest(List<double> reps, double v) => reps.MinBy(r => Math.Abs(r - v));
    }

    /// <summary>Система координат помещения: поворот вдоль самой длинной стены и при горизонтальных разрезах — перестановка осей.</summary>
    private sealed class Frame
    {
        private readonly double _cos;
        private readonly double _sin;
        private readonly PlanPoint _origin;
        private readonly bool _swap;

        private Frame(double angle, PlanPoint origin, bool swap)
        {
            (_cos, _sin, _origin, _swap) = (Math.Cos(angle), Math.Sin(angle), origin, swap);
            RowAngle = angle + (swap ? Math.PI / 2 : 0);
            if (angle == 0)
            {
                (_cos, _sin, _origin) = (1, 0, new PlanPoint(0, 0));
            }
        }

        public static Frame For(Polygon contour, bool swap)
        {
            var (a, b) = contour.Edges().MaxBy(e => Dist(e.A, e.B));
            var angle = Math.Atan2(b.YMm - a.YMm, b.XMm - a.XMm);
            angle -= Math.Floor(angle / (Math.PI / 2)) * (Math.PI / 2);
            if (angle < 1e-12 || (Math.PI / 2) - angle < 1e-12)
            {
                angle = 0;
            }

            return new Frame(angle, a, swap);
        }

        public List<PlanPoint> ToLocal(Polygon p) => p.Vertices.Select(ToLocal).ToList();

        public PlanPoint ToLocal(PlanPoint p)
        {
            double dx = p.XMm - _origin.XMm, dy = p.YMm - _origin.YMm;
            var local = new PlanPoint((dx * _cos) + (dy * _sin), (-dx * _sin) + (dy * _cos));
            return _swap ? new PlanPoint(local.YMm, local.XMm) : local;
        }

        /// <summary>Направление общих линий (рядов) на плане: ось X системы помещения, при перестановке осей — ось Y.</summary>
        public double RowAngle { get; }

        public PlanPoint Unswapped(PlanPoint local) => _swap ? new PlanPoint(local.YMm, local.XMm) : local;

        public PlanPoint ToWorld(PlanPoint local)
        {
            var p = Unswapped(local);
            return new PlanPoint(_origin.XMm + (p.XMm * _cos) - (p.YMm * _sin), _origin.YMm + (p.XMm * _sin) + (p.YMm * _cos));
        }

        public Polygon ToWorld(List<PlanPoint> local) => new(local.Select(ToWorld).ToList());
    }
}
