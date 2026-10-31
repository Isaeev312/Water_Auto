using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>Результат: ручные извещатели, ЗКСПС каждого автоматического ИП, число изоляторов КЗ и замечания.</summary>
public sealed record AlarmZoningResult(IReadOnlyList<PlacedManual> Manuals, IReadOnlyDictionary<string, string> ZoneOf, int Isolators, IReadOnlyList<Issue> Issues);

/// <summary>
/// Состав СПС после раскладки автоматических ИП (СП 484): ручные извещатели по п. 6.6.27, деление помещений на ЗКСПС по
/// площади (6.3.4), изоляторы короткого замыкания по п. 6.3.5 (единичная неисправность — не более 24 автоматических ИП и
/// одного ИПР). Выходы из помещений на чертеже не размечены, поэтому ИПР ставятся по правилу для неустановленной
/// планировки — не дальше нормативного расстояния от любой точки помещения, на стенах; проектировщик уточняет у выходов.
/// </summary>
public static class AlarmZoning
{
    private const string Doc = "СП 484.1311500.2020";

    /// <summary>Шаг кандидатов для ИПР вдоль стен и проверочных точек помещения, мм.</summary>
    private const double StepMm = 1000;

    /// <param name="manualIsolator">У выбранной модели ИПР встроенный изолятор КЗ — отдельный изолятор на неё не нужен.</param>
    public static AlarmZoningResult Build(IReadOnlyList<DetectorZone> zones, DetectorLayout layout, INormsProvider norms, bool manualIsolator)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(norms);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var issues = new List<Issue>();
        var manualRule = norms.Find("sp484.p6_6_27.manual");
        var zoneRule = norms.Find(ZoneChecks.ZoneId);
        var faultRule = norms.Find("sp484.p6_3_5.singleFault");
        foreach (var (id, r) in new[] { ("sp484.p6_6_27.manual", manualRule), (ZoneChecks.ZoneId, zoneRule), ("sp484.p6_3_5.singleFault", faultRule) }.Where(x => x.Item2 is null))
        {
            issues.Add(new Issue("aps.zone.noNorm", IssueLevel.Error, $"В данных норм нет записи {id} (ИПР, ЗКСПС, изоляторы)."));
        }

        if (manualRule is null || zoneRule is null || faultRule is null)
        {
            return new([], new Dictionary<string, string>(), 0, issues);
        }

        var reach = manualRule.Values["maxToAnyPointUnknownLayout"]!.Value * 1000;
        var manuals = new List<PlacedManual>();
        var zoneOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var zoneNo = 0;
        var maxArea = zoneRule.Values["maxArea"]!.Value;
        var perFault = (int)faultRule.Values["maxAutoDetectors"]!.Value;
        var isolators = 0;

        foreach (var z in zones)
        {
            // ИПР — на стенах, чтобы любая точка была не дальше нормативного расстояния (планировка выходов не задана).
            var placed = PlaceManuals(z.Outline, reach);
            manuals.AddRange(placed.Select((p, i) => new PlacedManual($"{z.RoomId}.M{i + 1}", z.RoomId, p)));

            // ЗКСПС: помещение целиком или полосами по длинной стороне, когда площадь больше предела.
            var detectors = layout.Detectors.Where(d => d.RoomId == z.RoomId).ToList();
            var area = z.Outline.AreaMm2 / 1e6;
            var parts = Math.Max(1, (int)Math.Ceiling(area / maxArea));
            var xs = z.Outline.Vertices.Select(v => v.XMm).ToList(); var ys = z.Outline.Vertices.Select(v => v.YMm).ToList();
            var alongX = xs.Max() - xs.Min() >= ys.Max() - ys.Min();
            var lo = alongX ? xs.Min() : ys.Min();
            var span = alongX ? xs.Max() - xs.Min() : ys.Max() - ys.Min();
            for (var k = 0; k < parts; k++)
            {
                zoneNo++;
                // Полоса k — по координате вдоль длинной стороны, чтобы ряды ИП не рвались между зонами.
                var from = lo + (span * k / parts);
                var to = k == parts - 1 ? double.PositiveInfinity : lo + (span * (k + 1) / parts);
                var inStrip = detectors.Where(d => { var c = alongX ? d.Location.XMm : d.Location.YMm; return c >= from && c < to; }).ToList();
                foreach (var d in inStrip)
                {
                    zoneOf[d.Id] = $"ЗКСПС-{zoneNo}";
                }

                // Изоляторы: группа автоматических ИП не больше предела единичной неисправности — по изолятору на группу.
                isolators += (inStrip.Count + perFault - 1) / perFault;
            }

            if (parts > 1)
            {
                issues.Add(new Issue("aps.zone.parts", IssueLevel.Info,
                    string.Create(ru, $"Помещение «{z.RoomName}» ({area:0} м²) разделено на {parts} ЗКСПС полосами по длинной стороне — деление условное, уточняется проектировщиком."),
                    z.RoomId, Doc + ", п. 6.3.4", z.Outline.Centroid));
            }
        }

        if (!manualIsolator)
        {
            isolators += manuals.Count;
        }

        if (manuals.Count > 0)
        {
            issues.Add(new Issue("aps.manual.noExits", IssueLevel.Info,
                string.Create(ru, $"Выходы из помещений на чертеже не размечены: ИПР ({manuals.Count} шт.) расставлены на стенах так, чтобы любая точка была не дальше {reach / 1000:0} м (правило для неустановленной планировки); при известных выходах ИПР ставятся у выходов, не дальше {manualRule.Values["maxBetweenIndoor"]:0} м друг от друга и {manualRule.Values["maxToRoomExit"]:0} м от выхода, на высоте {manualRule.Values["mountHeight"]:0.0} м."),
                Clause: Doc + ", п. 6.6.27"));
        }

        if (isolators > 0)
        {
            issues.Add(new Issue("aps.isolators", IssueLevel.Info,
                string.Create(ru, $"Изоляторов КЗ — {isolators} шт.: по одному на группу не более {perFault} автоматических ИП в пределах ЗКСПС{(manualIsolator ? "" : " и на каждый ИПР без встроенного изолятора")}; расстановка по линии — по топологии (кольцо или дерево), проектировщик."),
                Clause: Doc + ", п. 6.3.5"));
        }

        return new(manuals, zoneOf, isolators, issues);
    }

    /// <summary>Жадное покрытие: кандидаты вдоль стен, проверочные точки внутри; берётся кандидат, закрывающий больше непокрытых.</summary>
    private static List<PlanPoint> PlaceManuals(Polygon outline, double reach)
    {
        var candidates = new List<PlanPoint>();
        foreach (var (a, b) in outline.Edges())
        {
            var len = Math.Sqrt(((b.XMm - a.XMm) * (b.XMm - a.XMm)) + ((b.YMm - a.YMm) * (b.YMm - a.YMm)));
            var n = Math.Max(1, (int)Math.Ceiling(len / StepMm));
            for (var i = 0; i <= n; i++)
            {
                candidates.Add(new PlanPoint(a.XMm + ((b.XMm - a.XMm) * i / n), a.YMm + ((b.YMm - a.YMm) * i / n)));
            }
        }

        var xs = outline.Vertices.Select(v => v.XMm).ToList(); var ys = outline.Vertices.Select(v => v.YMm).ToList();
        var samples = new List<PlanPoint>();
        for (var x = xs.Min(); x <= xs.Max() + 1e-6; x += StepMm)
        {
            for (var y = ys.Min(); y <= ys.Max() + 1e-6; y += StepMm)
            {
                var p = new PlanPoint(x, y);
                if (outline.Contains(p)) { samples.Add(p); }
            }
        }

        samples.AddRange(outline.Vertices);
        var r2 = reach * reach;
        var uncovered = new HashSet<int>(Enumerable.Range(0, samples.Count));
        var result = new List<PlanPoint>();
        while (uncovered.Count > 0 && result.Count < 1000)
        {
            PlanPoint? best = null;
            var bestGain = 0;
            foreach (var c in candidates)
            {
                var gain = uncovered.Count(i => D2(samples[i], c) <= r2);
                if (gain > bestGain) { bestGain = gain; best = c; }
            }

            if (best is not { } chosen)
            {
                break;
            }

            result.Add(chosen);
            uncovered.RemoveWhere(i => D2(samples[i], chosen) <= r2);
        }

        return result;
    }

    private static double D2(PlanPoint a, PlanPoint b) => ((a.XMm - b.XMm) * (a.XMm - b.XMm)) + ((a.YMm - b.YMm) * (a.YMm - b.YMm));
}
