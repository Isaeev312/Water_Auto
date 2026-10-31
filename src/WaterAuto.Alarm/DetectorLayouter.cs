using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>
/// Раскладка точечных ИП (ADR 0012, этап 2): прямоугольные помещения, равномерная сетка; каждая точка помещения
/// контролируется нужным числом ИП (6.6.1, 6.6.2) — это проверяется по сетке точек, а не предполагается. Балки одного
/// направления делят помещение на поля; шаг поперёк балок и расстояние до граней — табл. 6.2. Кессоны (табл. 6.3),
/// короба и наклонные потолки (6.6.35, 6.6.39) — следующий срез; зоны с ними получают замечание.
/// </summary>
public static class DetectorLayouter
{
    /// <summary>Шаг проверочной сетки точек помещения, мм.</summary>
    private const double SampleMm = 250;

    private const int MaxDensify = 40;

    public static DetectorLayout Layout(IEnumerable<DetectorZone> zones, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(norms);
        var detectors = new List<PlacedDetector>();
        var counts = new Dictionary<string, int>();
        var issues = new List<Issue>();
        foreach (var zone in zones)
        {
            var placed = LayoutZone(zone, norms, issues);
            detectors.AddRange(placed);
            counts[zone.RoomId] = placed.Count;
        }

        return new DetectorLayout(detectors, counts, issues);
    }

    private static List<PlacedDetector> LayoutZone(DetectorZone zone, INormsProvider norms, List<Issue> issues)
    {
        var (rules, ruleIssues) = DetectorRules.Resolve(norms, zone.Kind, zone.Algorithm, zone.Addressable);
        issues.AddRange(ruleIssues);
        if (rules is null)
        {
            return [];
        }

        var clause = DetectorRules.Clause(zone.Kind);
        if (zone.CeilingHeight.Meters > rules.MaxRoomHeight.Meters + 1e-9)
        {
            issues.Add(new Issue("aps.layout.height", IssueLevel.Error,
                $"Помещение «{zone.RoomName}» высотой {zone.CeilingHeight} выше предела {rules.MaxRoomHeight} для точечных {KindText(zone.Kind)} ИП: нужен другой тип ИП.",
                zone.RoomId, clause, zone.Outline.Centroid));
            return [];
        }

        var rectangular = Box.Of(zone.Outline) is not null;
        if (!rectangular && !IsOrthogonal(zone.Outline))
        {
            issues.Add(new Issue("aps.layout.nonOrthogonal", IssueLevel.Error,
                $"Помещение «{zone.RoomName}»: стена не параллельна и не перпендикулярна остальным — раскладка ИП выполняется только для прямоугольных и Г-, П-образных контуров (стены под углом — позже).",
                zone.RoomId, Location: zone.Outline.Centroid));
            return [];
        }

        var box = Box.Of(zone.Outline) ?? Box.Bounding(zone.Outline);
        if (!rectangular && zone.Beams.Count > 0)
        {
            issues.Add(new Issue("aps.layout.beamsIrregular", IssueLevel.Warning,
                $"Помещение «{zone.RoomName}»: контур не прямоугольный — балки при раскладке ИП не учтены (табл. 6.2 применяется только к прямоугольным помещениям); проверить вручную.",
                zone.RoomId, "СП 484.1311500.2020, табл. 6.2", zone.Outline.Centroid));
        }

        // Непрямоугольный контур: сетка по габаритам, точки вне контура и ближе отступа к стенам отбрасываются,
        // покрытие проверяется по самому контуру — сетка уплотняется, пока каждая его точка не контролируется.
        var fields = rectangular ? Fields(zone, box, norms, issues) : [new Field(box, false, null, null, zone.Outline)];
        var points = new List<PlanPoint>();
        foreach (var field in fields)
        {
            var grid = LayoutField(zone, rules, field, issues);
            if (grid is null)
            {
                return [];
            }

            points.AddRange(grid);
        }

        return [.. points.Select((p, i) => new PlacedDetector($"{zone.RoomId}.D{i + 1}", zone.RoomId, p, zone.Kind))];
    }

    /// <summary>Поле раскладки: прямоугольник и предел шага и расстояния до грани поперёк балок (null — без балок).</summary>
    private sealed record Field(Box Box, bool AcrossX, double? MaxAcross, double? MaxToFace, Polygon? Outline = null, double? MaxStep = null);

    /// <summary>
    /// Поля между балками одного направления, пересекающими помещение насквозь. Балки двух направлений — кессоны (табл. 6.3),
    /// пока не раскладываются: предупреждение и раскладка без балок.
    /// </summary>
    private static List<Field> Fields(DetectorZone zone, Box box, INormsProvider norms, List<Issue> issues)
    {
        var whole = new Field(box, false, null, null);
        if (zone.Beams.Count == 0)
        {
            return [whole];
        }

        var spanning = zone.Beams
            .Select(b => (Beam: b, Box: Box.Of(b.Outline)))
            .Where(x => x.Box is { } bb && (bb.X0 <= box.X0 + Polygon.ToleranceMm && bb.X1 >= box.X1 - Polygon.ToleranceMm || bb.Y0 <= box.Y0 + Polygon.ToleranceMm && bb.Y1 >= box.Y1 - Polygon.ToleranceMm))
            .Select(x => (x.Beam, Box: x.Box!.Value, AlongX: x.Box!.Value.X0 <= box.X0 + Polygon.ToleranceMm && x.Box!.Value.X1 >= box.X1 - Polygon.ToleranceMm))
            .ToList();
        if (spanning.Count < zone.Beams.Count)
        {
            issues.Add(new Issue("aps.layout.beamsPartial", IssueLevel.Warning,
                $"Помещение «{zone.RoomName}»: {zone.Beams.Count - spanning.Count} балок не пересекают помещение насквозь или не прямоугольны — при раскладке ИП не учтены.",
                zone.RoomId, "СП 484.1311500.2020, табл. 6.2", zone.Outline.Centroid));
        }

        if (spanning.Count == 0)
        {
            return [whole];
        }

        if (spanning.Any(x => x.AlongX) && spanning.Any(x => !x.AlongX))
        {
            return Coffers(zone, box, spanning.Select(x => (x.Box, x.AlongX, x.Beam.Height.Meters)).ToList(), norms, issues);
        }

        // Балки вдоль X делят помещение по Y (поперёк балок — ось Y) и наоборот.
        var alongX = spanning[0].AlongX;
        var tallest = spanning.Max(x => x.Beam.Height.Meters);
        var row = DetectorRules.BeamSpacing(norms, zone.Kind, zone.CeilingHeight, new Length(tallest));
        if (row is null)
        {
            issues.Add(new Issue("aps.layout.noNorm", IssueLevel.Error, $"В данных норм нет строки табл. 6.2 для H = {zone.CeilingHeight}, D = {new Length(tallest)}.", zone.RoomId, "СП 484.1311500.2020, табл. 6.2"));
            return [];
        }

        issues.Add(new Issue("aps.layout.beams", IssueLevel.Info,
            $"Помещение «{zone.RoomName}»: {spanning.Count} балок высотой до {new Length(tallest)} при H = {zone.CeilingHeight} — ИП в полях между балками, поперёк балок не дальше {row.Value.Across} друг от друга и {row.Value.ToWall} от грани (строка {row.Value.RecordId}).",
            zone.RoomId, "СП 484.1311500.2020, табл. 6.2", zone.Outline.Centroid));

        var cuts = spanning.Select(x => alongX ? (x.Box.Y0, x.Box.Y1) : (x.Box.X0, x.Box.X1)).OrderBy(c => c.Item1).ToList();
        var fields = new List<Field>();
        var start = alongX ? box.Y0 : box.X0;
        var end = alongX ? box.Y1 : box.X1;
        foreach (var (c0, c1) in cuts)
        {
            if (c0 - start > Polygon.ToleranceMm)
            {
                fields.Add(new Field(alongX ? new Box(box.X0, start, box.X1, c0) : new Box(start, box.Y0, c0, box.Y1), !alongX, row.Value.Across.Millimeters, row.Value.ToWall.Millimeters));
            }

            start = Math.Max(start, c1);
        }

        if (end - start > Polygon.ToleranceMm)
        {
            fields.Add(new Field(alongX ? new Box(box.X0, start, box.X1, end) : new Box(start, box.Y0, end, box.Y1), !alongX, row.Value.Across.Millimeters, row.Value.ToWall.Millimeters));
        }

        return fields;
    }

    /// <summary>
    /// Кессоны (табл. 6.3): ячейки между балками двух направлений; в каждой ячейке ИП так, чтобы любая её точка была не дальше
    /// табличного расстояния от ближайшего ИП (шаг сетки не больше d·√2) и в радиусе зоны контроля. Балки ниже 10 % H —
    /// как плоский потолок. Место установки (на потолке или на нижней плоскости балок при W ≤ 4D) — в замечании.
    /// </summary>
    private static List<Field> Coffers(DetectorZone zone, Box box, List<(Box Box, bool AlongX, double HeightM)> beams, INormsProvider norms, List<Issue> issues)
    {
        var tallest = beams.Max(b => b.HeightM);
        var row = DetectorRules.CofferSpacing(norms, zone.Kind, zone.CeilingHeight, new Length(tallest));
        if (row is null)
        {
            issues.Add(new Issue("aps.layout.coffers", IssueLevel.Info,
                $"Помещение «{zone.RoomName}»: балки двух направлений ниже 10 % высоты помещения — размещение ИП как при плоском потолке (табл. 6.3).",
                zone.RoomId, "СП 484.1311500.2020, табл. 6.3", zone.Outline.Centroid));
            return [new Field(box, false, null, null)];
        }

        var xCuts = beams.Where(b => !b.AlongX).Select(b => (b.Box.X0, b.Box.X1)).OrderBy(c => c.Item1).ToList();
        var yCuts = beams.Where(b => b.AlongX).Select(b => (b.Box.Y0, b.Box.Y1)).OrderBy(c => c.Item1).ToList();
        var xs = Spans(box.X0, box.X1, xCuts);
        var ys = Spans(box.Y0, box.Y1, yCuts);
        var d = row.Value.Distance.Millimeters;
        var step = d * Math.Sqrt(2);
        var fields = new List<Field>();
        foreach (var (y0, y1) in ys)
        {
            foreach (var (x0, x1) in xs)
            {
                fields.Add(new Field(new Box(x0, y0, x1, y1), false, null, null, null, step));
            }
        }

        var minCell = Math.Min(xs.Min(s => s.Item2 - s.Item1), ys.Min(s => s.Item2 - s.Item1)) / 1000;
        var onBeams = minCell <= 4 * tallest;
        issues.Add(new Issue("aps.layout.coffers", IssueLevel.Info,
            string.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), $"Помещение «{zone.RoomName}»: кессоны {xs.Count} × {ys.Count} ячеек при балках до {new Length(tallest)}, H = {zone.CeilingHeight} — в каждой ячейке ИП не дальше {row.Value.Distance} от любой её точки (строка {row.Value.RecordId}); установка — {(onBeams ? "на нижней плоскости балок (W ≤ 4D)" : "на потолке (W > 4D)")}."),
            zone.RoomId, "СП 484.1311500.2020, табл. 6.3", zone.Outline.Centroid));
        return fields;
    }

    private static List<(double, double)> Spans(double start, double end, List<(double, double)> cuts)
    {
        var spans = new List<(double, double)>();
        var s = start;
        foreach (var (c0, c1) in cuts)
        {
            if (c0 - s > Polygon.ToleranceMm) { spans.Add((s, c0)); }
            s = Math.Max(s, c1);
        }

        if (end - s > Polygon.ToleranceMm) { spans.Add((s, end)); }
        return spans;
    }

    /// <summary>Сетка ИП в поле; null — покрытие не обеспечить (ошибка уже записана).</summary>
    private static List<PlanPoint>? LayoutField(DetectorZone zone, DetectorRules rules, Field field, List<Issue> issues)
    {
        var box = field.Box;
        var outline = field.Outline ?? new Polygon([new(box.X0, box.Y0), new(box.X1, box.Y0), new(box.X1, box.Y1), new(box.X0, box.Y1)]);
        var r = rules.Radius.Millimeters;
        var wall = rules.MinToWall.Millimeters;
        // Начальный шаг: при однократном контроле ячейка сетки — квадрат, вписанный в круг радиуса R (сторона R·√2);
        // при двукратном — сторона R. Дальше сетка уплотняется, пока проверочные точки не покрыты нужным числом ИП.
        var step = rules.Coverage >= 2 ? r : r * Math.Sqrt(2);
        var nx = Math.Max(1, (int)Math.Ceiling(box.Width / step));
        var ny = Math.Max(1, (int)Math.Ceiling(box.Height / step));
        if (field.MaxStep is { } maxStep)
        {
            // Кессон: любая точка ячейки не дальше табличного расстояния от ИП — шаг не больше d·√2 по обеим осям.
            nx = Math.Max(nx, (int)Math.Ceiling(box.Width / maxStep));
            ny = Math.Max(ny, (int)Math.Ceiling(box.Height / maxStep));
        }

        if (field.MaxAcross is { } across && field.MaxToFace is { } toFace)
        {
            // Поперёк балок: шаг не больше табличного и крайние ИП не дальше табличного расстояния от грани.
            var size = field.AcrossX ? box.Width : box.Height;
            var n = Math.Max((int)Math.Ceiling(size / across), size / 2 > toFace ? Math.Max(2, (int)Math.Ceiling((size - (2 * toFace)) / across) + 1) : 1);
            if (field.AcrossX) { nx = Math.Max(nx, n); } else { ny = Math.Max(ny, n); }
        }

        List<PlanPoint> grid = [];
        for (var i = 0; i < MaxDensify; i++)
        {
            grid = Grid(box, nx, ny, wall, field.AcrossX ? field.MaxToFace : null, field.AcrossX ? null : field.MaxToFace);
            if (field.Outline is not null)
            {
                grid = grid.Where(p => outline.Contains(p) && outline.DistanceTo(p) >= wall - Polygon.ToleranceMm).ToList();
            }

            if (MinCoverage(outline, grid, r) >= rules.Coverage)
            {
                break;
            }

            // Уплотнять ту ось, где ячейка крупнее.
            if (box.Width / nx >= box.Height / ny) { nx++; } else { ny++; }
        }

        if (MinCoverage(outline, grid, r) < rules.Coverage)
        {
            issues.Add(new Issue("aps.layout.coverage", IssueLevel.Error,
                $"Помещение «{zone.RoomName}»: не удалось обеспечить контроль каждой точки {rules.Coverage} ИП.",
                zone.RoomId, zone.Algorithm == DetectionAlgorithm.C ? "СП 484.1311500.2020, п. 6.6.2" : "СП 484.1311500.2020, п. 6.6.1", outline.Centroid));
            return null;
        }

        if (box.Width < 2 * wall || box.Height < 2 * wall)
        {
            issues.Add(new Issue("aps.layout.wallOffset", IssueLevel.Info,
                $"Помещение «{zone.RoomName}»: поле уже {new Length(2 * rules.MinToWall.Meters)} — отступ {rules.MinToWall} от стен (граней балок) не выдержать, ИП размещены по центру.",
                zone.RoomId, "СП 484.1311500.2020, п. 6.6.36", outline.Centroid));
        }

        // Шаг сетки по осям, где ИП больше одного; меньше 1,2 м — поле мало для нужной кратности контроля.
        var axisSteps = new List<double>();
        if (nx > 1) { axisSteps.Add(box.Width / nx); }
        if (ny > 1) { axisSteps.Add(box.Height / ny); }
        if (axisSteps.Count > 0 && axisSteps.Min() < rules.MinSpacing.Millimeters - 1e-6)
        {
            issues.Add(new Issue("aps.layout.spacing", IssueLevel.Warning,
                $"Помещение «{zone.RoomName}»: расстояние между ИП меньше {rules.MinSpacing} — помещение (поле между балками) слишком мало для {rules.Coverage}-кратного контроля точечными ИП.",
                zone.RoomId, "СП 484.1311500.2020, п. 6.5.1", outline.Centroid));
        }

        return grid;
    }

    /// <summary>Центры ячеек nx × ny, но не ближе отступа к стене и не дальше предела до грани балки (если задан); в узком поле — по центру.</summary>
    private static List<PlanPoint> Grid(Box box, int nx, int ny, double wall, double? maxEdgeX, double? maxEdgeY)
    {
        var xs = Axis(box.X0, box.Width, nx, wall, maxEdgeX);
        var ys = Axis(box.Y0, box.Height, ny, wall, maxEdgeY);
        return [.. ys.SelectMany(y => xs.Select(x => new PlanPoint(x, y)))];
    }

    private static List<double> Axis(double start, double size, int n, double wall, double? maxEdge)
    {
        if (n == 1 || size <= 2 * wall)
        {
            return Enumerable.Range(0, n).Select(i => start + (size * (i + 0.5) / n)).ToList();
        }

        // Крайние ИП — на половине ячейки от стены (не ближе отступа, не дальше предела до грани), остальные — равномерно.
        var cell = size / n;
        var first = Math.Max(wall, Math.Min(cell / 2, maxEdge ?? double.PositiveInfinity));
        return Enumerable.Range(0, n).Select(i => start + first + ((size - (2 * first)) * i / (n - 1))).ToList();
    }

    private static int MinCoverage(Polygon outline, List<PlanPoint> detectors, double radius)
    {
        var xs = outline.Vertices.Select(v => v.XMm).ToList();
        var ys = outline.Vertices.Select(v => v.YMm).ToList();
        var min = int.MaxValue;
        var r2 = (radius + Polygon.ToleranceMm) * (radius + Polygon.ToleranceMm);
        for (var x = xs.Min(); x <= xs.Max() + 1e-6; x = Math.Min(x + SampleMm, x < xs.Max() ? xs.Max() : double.PositiveInfinity))
        {
            for (var y = ys.Min(); y <= ys.Max() + 1e-6; y = Math.Min(y + SampleMm, y < ys.Max() ? ys.Max() : double.PositiveInfinity))
            {
                var n = detectors.Count(d => ((d.XMm - x) * (d.XMm - x)) + ((d.YMm - y) * (d.YMm - y)) <= r2);
                min = Math.Min(min, n);
            }
        }

        return min;
    }

    private static string KindText(DetectorKind kind) => kind == DetectorKind.Smoke ? "дымовых" : "тепловых";

    /// <summary>Все стороны контура параллельны осям (прямоугольник, Г-, П-образный контур).</summary>
    private static bool IsOrthogonal(Polygon p) =>
        p.Edges().All(e => Math.Abs(e.A.XMm - e.B.XMm) <= Polygon.ToleranceMm || Math.Abs(e.A.YMm - e.B.YMm) <= Polygon.ToleranceMm);

    /// <summary>Осевой прямоугольник контура; null — контур не прямоугольник (по площади с допуском).</summary>
    private readonly record struct Box(double X0, double Y0, double X1, double Y1)
    {
        public double Width => X1 - X0;

        public double Height => Y1 - Y0;

        public static Box Bounding(Polygon p) => new(p.Vertices.Min(v => v.XMm), p.Vertices.Min(v => v.YMm), p.Vertices.Max(v => v.XMm), p.Vertices.Max(v => v.YMm));

        public static Box? Of(Polygon p)
        {
            var b = new Box(p.Vertices.Min(v => v.XMm), p.Vertices.Min(v => v.YMm), p.Vertices.Max(v => v.XMm), p.Vertices.Max(v => v.YMm));
            var rect = Math.Abs(b.Width * b.Height - p.AreaMm2) <= Polygon.ToleranceMm * (b.Width + b.Height) * 2;
            return rect && p.Vertices.All(v => (Math.Abs(v.XMm - b.X0) <= Polygon.ToleranceMm || Math.Abs(v.XMm - b.X1) <= Polygon.ToleranceMm)
                && (Math.Abs(v.YMm - b.Y0) <= Polygon.ToleranceMm || Math.Abs(v.YMm - b.Y1) <= Polygon.ToleranceMm)) ? b : null;
        }
    }
}
