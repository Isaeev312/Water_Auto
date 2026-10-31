using System.Globalization;
using WaterAuto.Calc.Layout;
using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Ороситель каталога: K, рабочий диапазон давлений, защищаемая площадь и средняя интенсивность по давлению.</summary>
public sealed record SprinklerCandidate(
    string Id,
    string Maker,
    string Model,
    double K,
    double PMin,
    double PMax,
    double? AreaM2,
    IReadOnlyList<(double P, double I)> Intensity);

/// <summary>Строка перебора: подходит ли модель, почему нет, расход и давление насоса, Q·P.</summary>
public sealed record SprinklerOption(SprinklerCandidate Candidate, bool Valid, string Reason, Flow PumpFlow, Pressure PumpPressure, double Power);

public sealed record SprinklerSelectionResult(
    SprinklerCandidate? Chosen,
    IReadOnlyList<DesignResult>? Designs,
    IReadOnlyList<SprinklerOption> Rows,
    IReadOnlyList<Issue> Issues)
{
    /// <summary>Расчёт с наибольшим давлением насоса для выбранной модели.</summary>
    public DesignResult? Design => Designs?.MaxBy(d => d.PumpPressure.Mpa);
}

/// <summary>
/// Автоподбор оросителя из каталога: для каждой модели — полный проектный расчёт (<see cref="DesignCalculation"/>).
/// Модель подходит, если давление у оросителей зоны в паспортном диапазоне, средняя интенсивность по каталогу при давлении
/// диктующего не меньше нормативной (ниже данных каталога — не подтверждена) и защищаемая площадь не меньше l·b.
/// Из подходящих берётся модель с наименьшим Q·P насоса, при равенстве — с меньшим K. Это правило выбора программы, а не норма.
/// </summary>
public static class SprinklerSelection
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static SprinklerSelectionResult Select(
        IReadOnlyList<PipeRoute> routes,
        INormsProvider norms,
        LayoutRules rules,
        DesignSettings settings,
        FireParameters? parameters,
        HydrantInput? hydrants,
        IReadOnlyList<SprinklerCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return Select([.. routes.Select(r => (r, rules, parameters))], norms, settings, hydrants, candidates);
    }

    /// <summary>Подбор для трасс с разными правилами и параметрами (помещения разных групп): модель должна подходить всем.</summary>
    public static SprinklerSelectionResult Select(
        IReadOnlyList<(PipeRoute Route, LayoutRules Rules, FireParameters? Parameters)> routes,
        INormsProvider norms,
        DesignSettings settings,
        HydrantInput? hydrants,
        IReadOnlyList<SprinklerCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(candidates);
        var byK = new Dictionary<double, List<DesignResult>>();
        var rows = new List<(SprinklerOption Row, List<DesignResult> Designs)>();
        foreach (var c in candidates)
        {
            if (!byK.TryGetValue(c.K, out var designs))
            {
                designs = routes.Select(r => DesignCalculation.Run(r.Route, norms, r.Rules, settings with { SprinklerK = new KFactor(c.K) }, r.Parameters, hydrants)).ToList();
                byK[c.K] = designs;
            }

            var worst = designs.MaxBy(d => d.PumpPressure.Mpa)!;
            var reason = Reason(c, designs);
            rows.Add((new SprinklerOption(c, reason is null, reason ?? "подходит", worst.PumpFlow, worst.PumpPressure, worst.PumpFlow.LitersPerSecond * worst.PumpPressure.Mpa), designs));
        }

        var best = rows.Where(r => r.Row.Valid).OrderBy(r => r.Row.Power).ThenBy(r => r.Row.Candidate.K).Select(r => ((SprinklerOption, List<DesignResult>)?)r).FirstOrDefault();
        var issues = new List<Issue>();
        if (best is null)
        {
            issues.Add(new Issue("sprinkler.noneSuitable", IssueLevel.Error, $"Ни одна из {candidates.Count} моделей каталога не подходит: {string.Join("; ", rows.Select(r => $"{r.Row.Candidate.Model} — {r.Row.Reason}").Take(6))}."));
            return new SprinklerSelectionResult(null, null, rows.Select(r => r.Row).ToList(), issues);
        }

        return new SprinklerSelectionResult(best.Value.Item1.Candidate, best.Value.Item2, rows.Select(r => r.Row).ToList(), issues);
    }

    // null — модель подходит; иначе причина.
    private static string? Reason(SprinklerCandidate c, List<DesignResult> designs)
    {
        foreach (var d in designs)
        {
            var pDict = d.Hydrants is { Raised: true } h ? h.DictatingPressureActual.Mpa : d.DictatingPressure.Mpa;
            var pMax = d.SprinklerFlows.Values.Select(q => Math.Pow(q.LitersPerSecond / (10 * c.K), 2)).DefaultIfEmpty(pDict).Max();
            if (pDict < c.PMin - 1e-9)
            {
                return string.Create(Ru, $"давление у диктующего {pDict:0.000} МПа меньше {c.PMin} МПа");
            }

            if (pMax > c.PMax + 1e-9)
            {
                return string.Create(Ru, $"давление в зоне до {pMax:0.000} МПа больше {c.PMax} МПа");
            }

            if (c.AreaM2 is { } area && d.AreaPerSprinkler > area + 1e-9)
            {
                return string.Create(Ru, $"площадь на ороситель {d.AreaPerSprinkler:0.0} м² больше защищаемой {area} м²");
            }

            // Несколько значений при одном давлении (разные высоты установки) — берётся меньшее.
            var points = c.Intensity.GroupBy(x => x.P).Select(g => (P: g.Key, I: g.Min(x => x.I))).OrderBy(x => x.P).ToList();
            if (points.Count == 0 || pDict < points[0].P - 1e-9)
            {
                return string.Create(Ru, $"интенсивность при {pDict:0.000} МПа по каталогу не подтверждена (данные от {(points.Count > 0 ? points[0].P : 0)} МПа)");
            }

            var i = Interpolate(points, pDict);
            if (i < d.Parameters.Intensity - 1e-9)
            {
                return string.Create(Ru, $"интенсивность {i:0.###} л/(с·м²) меньше нормативной {d.Parameters.Intensity}");
            }
        }

        return null;
    }

    // Линейно между точками каталога; выше последней — значение последней точки (в запас).
    private static double Interpolate(List<(double P, double I)> points, double p)
    {
        for (var k = 1; k < points.Count; k++)
        {
            if (p <= points[k].P)
            {
                var (a, b) = (points[k - 1], points[k]);
                return a.I + ((b.I - a.I) * (p - a.P) / (b.P - a.P));
            }
        }

        return points[^1].I;
    }
}
