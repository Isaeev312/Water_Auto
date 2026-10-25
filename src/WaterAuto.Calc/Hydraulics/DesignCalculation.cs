using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>
/// Данные проекта для расчёта: стандарт труб, K оросителя, высота диктующего оросителя над осью насоса,
/// подводящий трубопровод от насоса до точки ПТ_ВВОД и давление на входе насоса (гарантированный напор по ТУ).
/// </summary>
public sealed record DesignSettings(
    string PipeStandard,
    KFactor SprinklerK,
    Length DictatingAboveAxis,
    Length SupplyLength,
    int SupplyDn,
    Pressure InletPressure,
    double? DesignVelocity = null);

/// <summary>Участок расчётного пути от ввода к диктующему оросителю.</summary>
public sealed record PathSegment(string Id, string From, string To, int Dn, Length Length, PipeCharacteristic Kt, Flow Flow, Pressure LinearLoss, Pressure LossWithLocal, double Velocity);

public sealed record DesignResult(
    string DictatingSprinklerId,
    double DictatingFactor,
    Flow DictatingFlow,
    Pressure DictatingPressure,
    Length RowSpacing,
    Length LineSpacing,
    double AreaPerSprinkler,
    double ZoneArea,
    IReadOnlyList<string> ZoneSprinklers,
    IReadOnlyDictionary<string, Flow> SprinklerFlows,
    Flow DesignFlow,
    Flow NormativeFlow,
    Flow PumpFlow,
    string PumpFlowClause,
    double LocalLossFactor,
    IReadOnlyList<PathSegment> Path,
    Pressure InletNodePressure,
    SeriesPipe? SupplyPipe,
    Pressure SupplyLinearLoss,
    Pressure Elevation,
    Pressure PumpPressure,
    Length PumpHead,
    double SupplyVelocity,
    double VelocityLimit,
    double Duration,
    double WaterVolume,
    double TankVolume,
    double TankMaxFill,
    double JockeyTankMinLiters,
    IReadOnlyList<string> NormIds,
    PipeRoute Route,
    double? DesignVelocity,
    double DischargeCoefficient,
    double FirstSegmentDiameterMm,
    IReadOnlyList<Issue> Issues);

/// <summary>
/// Проектный расчёт спринклерной АУП по приложению Б СП 485 (ADR 0008):
/// диктующий ороситель — самый удалённый от ввода по трассе; расход и давление у него — Б.1 с верхним множителем (В-08);
/// диктующая зона — N = ⌈S / (l·b)⌉ ближайших к нему оросителей (Б.2, Б.3; форма зоны — В-01);
/// сеть — последовательный расчёт (Б.4–Б.23) с местными потерями 20 % на всех участках от диктующего оросителя
/// до насоса, включая узел управления (Б.1.3.13; В-02, В-03); расход насоса — Б.24–Б.26; давление — Б.30 с Z = H/100;
/// скорости — п. 6.7.1.37; запас воды — Q·t по табл. 6.1 с заполнением резервуара по п. 6.9.13.
/// </summary>
public static class DesignCalculation
{
    public static DesignResult Run(PipeRoute route, INormsProvider norms, LayoutRules rules, DesignSettings settings)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(norms);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<Issue>();
        var table = norms.Find(rules.NormIds[0])!;
        var factorRecord = norms.Find("sp485.b1_1_12.dictatingFlowFactor")!;
        var circle = norms.Find("sp485.b1_1_10.dictatingArea")!;
        var local = norms.Find("sp485.b1_3_13.localLossFactor")!;
        var velocity = norms.Find("sp485.p6_7_1_37.velocity")!;
        var jockey = norms.Find("sp485.p6_9_4.jockeyTank")!;
        var fill = norms.Find("sp485.p6_9_13.tankFill")!;

        var i = table.Values["i_water"]!.Value;
        var s = table.Values["S"]!.Value;
        var qNorm = table.Values["Q_water"]!.Value;
        var t = table.Values["t_min"]!.Value;
        var factor = factorRecord.Values["max"]!.Value;
        var k = local.Values["localLossFactor"]!.Value;
        var vMax = velocity.Values["pressureMax"]!.Value;

        // Б.1: q = (1,3…1,5)·i·s; p = (q / 10K)².
        var q1 = factor * i * circle.Values["s"]!.Value;
        var p1 = Math.Pow(q1 / (10 * settings.SprinklerK.Value), 2);

        var nodes = route.Nodes.ToDictionary(n => n.Id);
        var byTo = route.Segments.ToDictionary(x => x.To);
        var sprinklers = route.Nodes.Where(n => n.Kind == RouteNodeKind.Sprinkler).ToList();
        double PathLength(string id) => byTo.TryGetValue(id, out var seg) ? seg.Length.Meters + PathLength(seg.From) : 0;
        var dictating = sprinklers.MaxBy(n => PathLength(n.Id))!;

        // Б.3: условная площадь на ороситель l·b по сетке у диктующего оросителя.
        var (l, b) = Spacing(dictating, sprinklers);
        var area = l * b;
        var n = (int)Math.Ceiling((s / area) - 1e-9);
        var zone = sprinklers
            .OrderBy(x => Dist(x.Location, dictating.Location))
            .ThenByDescending(x => PathLength(x.Id))
            .Take(n)
            .Select(x => x.Id)
            .ToList();

        var series = new PipeSeries(norms, settings.PipeStandard);
        var diameter = norms.Find("sp485.b1_2_6.diameter")!;
        var mu = diameter.Values["muMin"]!.Value;
        var open = zone.ToHashSet();
        TreeSolution Solve(PipeRoute r)
        {
            var built = RouteNetwork.Build(r, norms, settings.PipeStandard, settings.SprinklerK, Length.FromMillimeters(0), 1 + k);
            var network = built.Network!;
            network = network with
            {
                Nodes = network.Nodes.Select(x => x.Kind == NodeKind.Sprinkler && !open.Contains(x.Id) ? x with { Kind = NodeKind.Tee, Emitter = null } : x).ToList(),
            };
            return TreeSolver.SolveDesign(network, dictating.Id, new Pressure(p1));
        }

        // Б.5: диаметры участков, по которым идёт расход, — по принятой скорости V; повтор до установления диаметров.
        var current = route;
        var solution = Solve(current);
        for (var iteration = 0; settings.DesignVelocity is { } v && iteration < 20; iteration++)
        {
            var resized = Resize(current, solution, series, mu, Math.Min(v, diameter.Values["velocityMax"]!.Value), issues);
            if (resized is null)
            {
                break;
            }

            current = resized;
            solution = Solve(current);
        }

        issues.AddRange(solution.Issues);
        byTo = current.Segments.ToDictionary(x => x.To);

        var path = new List<PathSegment>();
        for (var id = dictating.Id; byTo.TryGetValue(id, out var seg); id = seg.From)
        {
            var pipe = series.Pick(seg.Dn)!;
            var flow = solution.PipeFlows[seg.Id];
            var loss = solution.PipeLosses[seg.Id];
            path.Add(new PathSegment(seg.Id, seg.From, seg.To, pipe.Dn, seg.Length, pipe.Kt, flow, new Pressure(loss.Mpa / (1 + k)), loss, Velocity(flow.LitersPerSecond, pipe)));
        }

        path.Reverse();
        var qDesign = solution.TotalFlow.LitersPerSecond;
        var (qPump, clause) = qDesign <= qNorm ? (qNorm, "Б.24") : (qDesign, "Б.26");

        var supply = series.Pick(settings.SupplyDn);
        var supplyLinear = supply is null ? 0 : qPump * qPump * settings.SupplyLength.Meters / (100 * supply.Kt.Kt);
        var z = Pressure.FromHeight(settings.DictatingAboveAxis).Mpa;
        var pump = solution.SourcePressure.Mpa + ((1 + k) * supplyLinear) + z - settings.InletPressure.Mpa;
        var supplyVelocity = supply is null ? 0 : Velocity(qPump, supply);

        foreach (var x in path.Where(x => x.Velocity > vMax + 1e-9))
        {
            issues.Add(new Issue("design.velocity", IssueLevel.Warning, $"Участок {x.Id} DN{x.Dn}: скорость {x.Velocity:0.00} м/с больше {vMax} м/с — увеличить диаметр.", x.Id, "6.7.1.37"));
        }

        if (supplyVelocity > vMax + 1e-9)
        {
            issues.Add(new Issue("design.velocity", IssueLevel.Warning, $"Подводящий DN{settings.SupplyDn}: скорость {supplyVelocity:0.00} м/с больше {vMax} м/с — увеличить диаметр.", Clause: "6.7.1.37"));
        }

        var water = qPump * t * 60 / 1000;
        var maxFill = fill.Values["maxFill"]!.Value;
        issues.AddRange(series.Issues.Where(x => !issues.Contains(x)));
        return new DesignResult(
            dictating.Id,
            factor,
            new Flow(q1),
            new Pressure(p1),
            new Length(l),
            new Length(b),
            area,
            n * area,
            zone,
            solution.EmitterFlows,
            new Flow(qDesign),
            new Flow(qNorm),
            new Flow(qPump),
            clause,
            k,
            path,
            solution.SourcePressure,
            supply,
            new Pressure(supplyLinear),
            new Pressure(z),
            new Pressure(pump),
            new Length(pump * 100),
            supplyVelocity,
            vMax,
            t,
            water,
            water / (maxFill / 100),
            maxFill,
            jockey.Values["minVolume"]!.Value,
            [table.Id, factorRecord.Id, circle.Id, local.Id, velocity.Id, jockey.Id, fill.Id, diameter.Id],
            current,
            settings.DesignVelocity,
            mu,
            settings.DesignVelocity is { } dv ? DiameterMm(q1, mu, dv) : 0,
            issues);
    }

    /// <summary>Б.5: d = 1000·√(4Q / (π·μ·V)), Q — л/с, мм.</summary>
    public static double DiameterMm(double flowLps, double mu, double velocity) =>
        1000 * Math.Sqrt(4 * flowLps / 1000 / (Math.PI * mu * velocity));

    // Новые DN участков с расходом по Б.5; выше по течению DN не меньше, чем на участках за ним. null — без изменений.
    private static PipeRoute? Resize(PipeRoute route, TreeSolution solution, PipeSeries series, double mu, double velocity, List<Issue> issues)
    {
        var dn = route.Segments.ToDictionary(s => s.Id, s => series.Pick(s.Dn)?.Dn ?? s.Dn);
        foreach (var s in route.Segments)
        {
            var q = solution.PipeFlows.TryGetValue(s.Id, out var f) ? f.LitersPerSecond : 0;
            if (q <= 1e-9)
            {
                continue;
            }

            var pipe = series.WithInnerAtLeast(DiameterMm(q, mu, velocity), dn[s.Id]);
            if (pipe is null)
            {
                issues.Add(new Issue("design.noDiameter", IssueLevel.Error, $"Участок {s.Id}: в сортаменте нет трубы на расход {q:0.00} л/с при скорости {velocity} м/с.", s.Id, "Б.1.2.6"));
                return null;
            }

            dn[s.Id] = pipe.Dn;
        }

        var children = route.Segments.ToLookup(s => s.From);
        int Up(RouteSegment s) => dn[s.Id] = Math.Max(dn[s.Id], children[s.To].Select(Up).DefaultIfEmpty(0).Max());
        foreach (var root in route.Segments.Where(s => s.From == route.InletId))
        {
            Up(root);
        }

        var changed = route.Segments.Any(s => dn[s.Id] != (series.Pick(s.Dn)?.Dn ?? s.Dn));
        return changed ? route with { Segments = route.Segments.Select(s => s with { Dn = dn[s.Id] }).ToList() } : null;
    }

    // Шаг вдоль ряда l и между рядами b у оросителя: ближайший сосед и ближайший в перпендикулярном направлении, м.
    private static (double L, double B) Spacing(RouteNode at, List<RouteNode> all)
    {
        var others = all.Where(x => x.Id != at.Id).ToList();
        if (others.Count == 0)
        {
            return (1, 1);
        }

        var first = others.MinBy(x => Dist(x.Location, at.Location))!;
        double ux = first.Location.XMm - at.Location.XMm, uy = first.Location.YMm - at.Location.YMm;
        var l = Math.Sqrt((ux * ux) + (uy * uy));
        var across = others
            .Where(x =>
            {
                double vx = x.Location.XMm - at.Location.XMm, vy = x.Location.YMm - at.Location.YMm;
                return Math.Abs((ux * vx) + (uy * vy)) < 0.5 * l * Math.Sqrt((vx * vx) + (vy * vy));
            })
            .Select(x => Dist(x.Location, at.Location))
            .DefaultIfEmpty(l)
            .Min();
        return (l / 1000, across / 1000);
    }

    // v = Q / (π·d²/4), Q — л/с, d — внутренний диаметр, м/с.
    private static double Velocity(double flowLps, SeriesPipe pipe)
    {
        var d = (pipe.OuterDiameterMm - (2 * pipe.WallMm)) / 1000;
        return flowLps / 1000 / (Math.PI * d * d / 4);
    }

    private static double Dist(PlanPoint a, PlanPoint b) => Math.Sqrt(((a.XMm - b.XMm) * (a.XMm - b.XMm)) + ((a.YMm - b.YMm) * (a.YMm - b.YMm)));
}
