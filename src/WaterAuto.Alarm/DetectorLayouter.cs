using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>
/// Раскладка точечных ИП (ADR 0012, этап 2): прямоугольные помещения, равномерная сетка; каждая точка помещения
/// контролируется нужным числом ИП (6.6.1, 6.6.2) — это проверяется по сетке точек, а не предполагается. Балки,
/// короба и наклонные потолки (табл. 6.2, 6.3, пп. 6.6.35, 6.6.39) — следующий срез; зоны с ними получают замечание.
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

        if (Box.Of(zone.Outline) is not { } box)
        {
            issues.Add(new Issue("aps.layout.nonRectangular", IssueLevel.Error,
                $"Помещение «{zone.RoomName}»: контур не прямоугольный — раскладка ИП выполняется только для прямоугольных помещений (остальные — позже).",
                zone.RoomId, Location: zone.Outline.Centroid));
            return [];
        }

        var r = rules.Radius.Millimeters;
        var wall = rules.MinToWall.Millimeters;
        // Начальный шаг: при однократном контроле ячейка сетки — квадрат, вписанный в круг радиуса R (сторона R·√2);
        // при двукратном — сторона R. Дальше сетка уплотняется, пока проверочные точки не покрыты нужным числом ИП.
        var step = rules.Coverage >= 2 ? r : r * Math.Sqrt(2);
        var nx = Math.Max(1, (int)Math.Ceiling(box.Width / step));
        var ny = Math.Max(1, (int)Math.Ceiling(box.Height / step));
        List<PlanPoint> grid = [];
        for (var i = 0; i < MaxDensify; i++)
        {
            grid = Grid(box, nx, ny, wall);
            if (MinCoverage(zone.Outline, grid, r) >= rules.Coverage)
            {
                break;
            }

            // Уплотнять ту ось, где ячейка крупнее.
            if (box.Width / nx >= box.Height / ny) { nx++; } else { ny++; }
        }

        if (MinCoverage(zone.Outline, grid, r) < rules.Coverage)
        {
            issues.Add(new Issue("aps.layout.coverage", IssueLevel.Error,
                $"Помещение «{zone.RoomName}»: не удалось обеспечить контроль каждой точки {rules.Coverage} ИП.",
                zone.RoomId, zone.Algorithm == DetectionAlgorithm.C ? "СП 484.1311500.2020, п. 6.6.2" : "СП 484.1311500.2020, п. 6.6.1", zone.Outline.Centroid));
            return [];
        }

        if (box.Width < 2 * wall || box.Height < 2 * wall)
        {
            issues.Add(new Issue("aps.layout.wallOffset", IssueLevel.Info,
                $"Помещение «{zone.RoomName}» уже {new Length(2 * rules.MinToWall.Meters)}: отступ от стен {rules.MinToWall} не выдержать, ИП размещены по центру между стенами.",
                zone.RoomId, "СП 484.1311500.2020, п. 6.6.36", zone.Outline.Centroid));
        }

        // Шаг сетки по осям, где ИП больше одного; меньше 1,2 м — помещение мало для нужной кратности контроля.
        var axisSteps = new List<double>();
        if (nx > 1) { axisSteps.Add(box.Width / nx); }
        if (ny > 1) { axisSteps.Add(box.Height / ny); }
        if (axisSteps.Count > 0 && axisSteps.Min() < rules.MinSpacing.Millimeters - 1e-6)
        {
            issues.Add(new Issue("aps.layout.spacing", IssueLevel.Warning,
                $"Помещение «{zone.RoomName}»: расстояние между ИП меньше {rules.MinSpacing} — помещение слишком мало для {rules.Coverage}-кратного контроля точечными ИП.",
                zone.RoomId, "СП 484.1311500.2020, п. 6.5.1", zone.Outline.Centroid));
        }

        return [.. grid.Select((p, i) => new PlacedDetector($"{zone.RoomId}.D{i + 1}", zone.RoomId, p, zone.Kind))];
    }

    /// <summary>Центры ячеек nx × ny, но не ближе отступа к стене; в узком помещении — по центру.</summary>
    private static List<PlanPoint> Grid(Box box, int nx, int ny, double wall)
    {
        var xs = Axis(box.X0, box.Width, nx, wall);
        var ys = Axis(box.Y0, box.Height, ny, wall);
        return [.. ys.SelectMany(y => xs.Select(x => new PlanPoint(x, y)))];
    }

    private static List<double> Axis(double start, double size, int n, double wall)
    {
        if (n == 1 || size <= 2 * wall)
        {
            return Enumerable.Range(0, n).Select(i => start + (size * (i + 0.5) / n)).ToList();
        }

        // Крайние ИП — на отступе от стены, остальные — равномерно между ними: так покрытие углов не хуже, чем у центров ячеек.
        var cell = size / n;
        var first = Math.Min(cell / 2, Math.Max(wall, cell / 2));
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

    /// <summary>Осевой прямоугольник контура; null — контур не прямоугольник (по площади с допуском).</summary>
    private readonly record struct Box(double X0, double Y0, double X1, double Y1)
    {
        public double Width => X1 - X0;

        public double Height => Y1 - Y0;

        public static Box? Of(Polygon p)
        {
            var b = new Box(p.Vertices.Min(v => v.XMm), p.Vertices.Min(v => v.YMm), p.Vertices.Max(v => v.XMm), p.Vertices.Max(v => v.YMm));
            var rect = Math.Abs(b.Width * b.Height - p.AreaMm2) <= Polygon.ToleranceMm * (b.Width + b.Height) * 2;
            return rect && p.Vertices.All(v => (Math.Abs(v.XMm - b.X0) <= Polygon.ToleranceMm || Math.Abs(v.XMm - b.X1) <= Polygon.ToleranceMm)
                && (Math.Abs(v.YMm - b.Y0) <= Polygon.ToleranceMm || Math.Abs(v.YMm - b.Y1) <= Polygon.ToleranceMm)) ? b : null;
        }
    }
}
