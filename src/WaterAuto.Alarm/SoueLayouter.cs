using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>
/// Раскладка оповещателей СОУЭ (ADR 0012, этап 3) по СП 3.13130.2026: требуемый уровень звука — по фону помещения (6.8.2),
/// радиус действия оповещателя — по формуле В.2 от уровня на 1 м; звуковые и речевые — сеткой на потолке так, чтобы любая
/// точка была в радиусе ближайшего (без суммирования, в запас); для речевых шаг не больше 6.11.3; световые — на стенах
/// так, чтобы до любой точки было не больше 6.16.2. Фон, способ оповещения и модели — исходные данные, нормы — из данных.
/// </summary>
public static class SoueLayouter
{
    private const string Doc = "СП 3.13130.2026";
    private const double StepMm = 1000;

    public static SoueLayout Layout(IReadOnlyList<SoueZone> zones, NotifierModel sounder, NotifierModel light, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(sounder);
        ArgumentNullException.ThrowIfNull(light);
        ArgumentNullException.ThrowIfNull(norms);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var issues = new List<Issue>();
        var level = norms.Find("sp3.p6_8_2.soundLevel");
        var max = norms.Find("sp3.p6_8_4.soundMax");
        var lightRule = norms.Find("sp3.p6_16_2.lightNotifier");
        var mount = norms.Find("sp3.p6_18_1.mountHeight");
        var speech = norms.Find("sp3.p6_11_3.speech");
        var fault = norms.Find("sp3.p6_4_1.faultArea");
        var law = norms.Find("sp3.pB_formulas.attenuation");
        foreach (var (id, r) in new[] { ("sp3.p6_8_2.soundLevel", level), ("sp3.p6_8_4.soundMax", max), ("sp3.p6_16_2.lightNotifier", lightRule), ("sp3.p6_18_1.mountHeight", mount), ("sp3.p6_11_3.speech", speech), ("sp3.p6_4_1.faultArea", fault), ("sp3.pB_formulas.attenuation", law) }.Where(x => x.Item2 is null))
        {
            issues.Add(new Issue("soue.noNorm", IssueLevel.Error, $"В данных норм нет записи {id} (СОУЭ)."));
        }

        if (level is null || max is null || lightRule is null || mount is null || speech is null || fault is null || law is null)
        {
            return new([], [], [], issues);
        }

        var sounders = new List<PlacedNotifier>();
        var lights = new List<PlacedNotifier>();
        var results = new List<SoueZoneResult>();
        var coefficient = law.Values["distanceLawCoefficient"]!.Value;
        var r1 = law.Values["referenceDistance"]!.Value;
        var lightReach = lightRule.Values["maxDistance"]!.Value * 1000;
        if (sounder.LevelAt1m is not { } l1)
        {
            issues.Add(new Issue("soue.sounder.noLevel", IssueLevel.Error, $"У оповещателя {sounder.Name} в каталоге нет уровня звука на 1 м — радиус действия не определить (прил. В, В.3.1)."));
            return new([], [], [], issues);
        }

        foreach (var z in zones)
        {
            // Требуемый уровень: фон + 15 дБ, не ниже 65 дБА; при фоне выше 85 — 105 дБА и световое оповещение обязательно.
            var required = Math.Max(z.BackgroundDba + level.Values["aboveBackground"]!.Value, level.Values["min"]!.Value);
            if (z.BackgroundDba > level.Values["loudBackground"]!.Value)
            {
                required = Math.Max(required, level.Values["loudMin"]!.Value);
                issues.Add(new Issue("soue.light.required", IssueLevel.Info,
                    string.Create(ru, $"«{z.RoomName}»: фон {z.BackgroundDba:0} дБА выше {level.Values["loudBackground"]:0} дБА — звуковой уровень не менее {level.Values["loudMin"]:0} дБА и обязательно световое и (или) тактильное оповещение."),
                    z.RoomId, Doc + ", п. 6.8.2", z.Outline.Centroid));
            }

            if (required > max.Values["max"]!.Value)
            {
                issues.Add(new Issue("soue.level.max", IssueLevel.Error,
                    string.Create(ru, $"«{z.RoomName}»: требуемый уровень {required:0} дБА выше допустимого {max.Values["max"]:0} дБА — звуковое оповещение невозможно, нужен световой/тактильный способ."),
                    z.RoomId, Doc + ", п. 6.8.4", z.Outline.Centroid));
            }

            if (z.Method == WarningMethod.Speech)
            {
                required = Math.Max(z.BackgroundDba + speech.Values["aboveBackground"]!.Value, required);
                if (z.BackgroundDba > speech.Values["maxBackground"]!.Value)
                {
                    issues.Add(new Issue("soue.speech.background", IssueLevel.Error,
                        string.Create(ru, $"«{z.RoomName}»: фон {z.BackgroundDba:0} дБА выше {speech.Values["maxBackground"]:0} дБА — речевое оповещение неразборчиво; применить звуковой способ (прим. 3 к табл. Б.1) или снизить шум."),
                        z.RoomId, Doc + ", п. 6.11.3", z.Outline.Centroid));
                }
            }

            // В.2: L2 = L1 + 20·lg(r1/r2) → r2 = r1·10^((L1 − Lтреб)/20).
            var radiusM = r1 * Math.Pow(10, (l1 - required) / coefficient);
            var radius = new Length(radiusM);
            if (radiusM < 2)
            {
                // Модель не тянет помещение: раскладка дала бы сотни оповещателей и бессмысленный ток — остановить с ошибкой.
                issues.Add(new Issue("soue.sounder.weak", IssueLevel.Error,
                    string.Create(ru, $"«{z.RoomName}»: оповещатель {sounder.Name} ({l1:0} дБ на 1 м) даёт требуемые {required:0} дБА только в радиусе {radiusM:0.#} м — модель непригодна, выбрать оповещатель не ниже {required + coefficient * Math.Log10(5):0} дБ на 1 м (радиус 5 м) в alarm.warning.sounder."),
                    z.RoomId, Doc + ", прил. В, В.2", z.Outline.Centroid));
                results.Add(new SoueZoneResult(z.RoomId, required, radius, 0, 0));
                continue;
            }

            var step = radiusM * Math.Sqrt(2) * 1000;
            if (z.Method == WarningMethod.Speech)
            {
                step = Math.Min(step, speech.Values["spacingOneWay"]!.Value * 1000);
            }

            var placed = Grid(z.Outline, step);
            sounders.AddRange(placed.Select((p, i) => new PlacedNotifier($"{z.RoomId}.S{i + 1}", z.RoomId, p, z.Method == WarningMethod.Speech ? NotifierKind.Speech : NotifierKind.Sound)));

            var wall = WallCover(z.Outline, lightReach);
            lights.AddRange(wall.Select((p, i) => new PlacedNotifier($"{z.RoomId}.L{i + 1}", z.RoomId, p, NotifierKind.Light)));

            var area = z.Outline.AreaMm2 / 1e6;
            if (area > fault.Values["maxArea"]!.Value)
            {
                var parts = (int)Math.Ceiling(area / fault.Values["maxArea"]!.Value);
                issues.Add(new Issue("soue.zone.split", IssueLevel.Warning,
                    string.Create(ru, $"«{z.RoomName}» ({area:0} м²): единичная неисправность линии не должна отключать оповещение более чем на {fault.Values["maxArea"]:0} м² — не менее {parts} линий (зон) оповещения."),
                    z.RoomId, Doc + ", п. 6.4.1", z.Outline.Centroid));
            }

            results.Add(new SoueZoneResult(z.RoomId, required, radius, placed.Count, wall.Count));
        }

        if (sounders.Count > 0)
        {
            issues.Add(new Issue("soue.mountHeight", IssueLevel.Info,
                string.Create(ru, $"Оповещатели устанавливаются не ниже {mount.Values["minHeight"]:0.0} м от пола до низа корпуса; световые — на стенах {lightRule.Values["wallHeightMin"]:0.0}–{lightRule.Values["wallHeightMax"]:0.0} м или до {lightRule.Values["maxHeightOnPath"]:0.0} м над путём. Уровень звука — по ближайшему оповещателю без суммирования (в запас); отражения и преграды не учтены (В.6.2)."),
                Clause: Doc + ", пп. 6.18.1, 6.16.2"));
        }

        return new(sounders, lights, results, issues);
    }

    /// <summary>Равномерная сетка по габаритам помещения с шагом не больше заданного; точки вне контура отбрасываются.</summary>
    private static List<PlanPoint> Grid(Polygon outline, double stepMm)
    {
        var xs = outline.Vertices.Select(v => v.XMm).ToList(); var ys = outline.Vertices.Select(v => v.YMm).ToList();
        double w = xs.Max() - xs.Min(), h = ys.Max() - ys.Min();
        var nx = Math.Max(1, (int)Math.Ceiling(w / stepMm));
        var ny = Math.Max(1, (int)Math.Ceiling(h / stepMm));
        var points = new List<PlanPoint>();
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                var p = new PlanPoint(xs.Min() + (w * (i + 0.5) / nx), ys.Min() + (h * (j + 0.5) / ny));
                if (outline.Contains(p)) { points.Add(p); }
            }
        }

        return points;
    }

    /// <summary>Световые оповещатели на стенах: жадное покрытие точек помещения в заданном радиусе (как ИПР).</summary>
    private static List<PlanPoint> WallCover(Polygon outline, double reach)
    {
        var candidates = new List<PlanPoint>();
        foreach (var (a, b) in outline.Edges())
        {
            var len = Math.Sqrt(((b.XMm - a.XMm) * (b.XMm - a.XMm)) + ((b.YMm - a.YMm) * (b.YMm - a.YMm)));
            var n = Math.Max(1, (int)Math.Ceiling(len / StepMm));
            for (var i = 0; i <= n; i++) { candidates.Add(new PlanPoint(a.XMm + ((b.XMm - a.XMm) * i / n), a.YMm + ((b.YMm - a.YMm) * i / n))); }
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
            PlanPoint? best = null; var bestGain = 0;
            foreach (var c in candidates)
            {
                var gain = uncovered.Count(i => D2(samples[i], c) <= r2);
                if (gain > bestGain) { bestGain = gain; best = c; }
            }

            if (best is not { } chosen) { break; }
            result.Add(chosen);
            uncovered.RemoveWhere(i => D2(samples[i], chosen) <= r2);
        }

        return result;
    }

    private static double D2(PlanPoint a, PlanPoint b) => ((a.XMm - b.XMm) * (a.XMm - b.XMm)) + ((a.YMm - b.YMm) * (a.YMm - b.YMm));
}
