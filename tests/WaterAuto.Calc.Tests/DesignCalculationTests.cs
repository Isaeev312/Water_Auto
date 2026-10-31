using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class DesignCalculationTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);
    private static readonly LayoutRules Rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
    private static readonly KFactor K = new(0.47);

    private static readonly DesignSettings Settings = new(
        "ГОСТ 3262",
        K,
        DictatingAboveAxis: new Length(4.0),
        SupplyLength: new Length(25.0),
        SupplyDn: 100,
        InletPressure: new Pressure(0.10));

    private static DesignResult Calculate(double? velocity = null)
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var layout = SprinklerLayouter.Layout(plan, Rules);
        var route = PipeRouter.Route(plan, layout, Norms, Rules, new Pressure(0.3)).Routes[0];
        return DesignCalculation.Run(route, Norms, Rules, Settings with { DesignVelocity = velocity });
    }

    private static DesignResult CalculateWithHydrants(HydrantSettings s)
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var layout = SprinklerLayouter.Layout(plan, Rules);
        var route = PipeRouter.Route(plan, layout, Norms, Rules, new Pressure(0.3)).Routes[0];
        var demand = HydrantDemand.Resolve(Norms, s).Demand!;
        return DesignCalculation.Run(route, Norms, Rules, Settings, hydrants: new HydrantInput(s, demand));
    }

    [Fact]
    public void Hydrants_are_part_of_the_network_and_pump_flow_follows_B29()
    {
        // Б.1.3.16: краны — потребители сети при давлении узла; Б.29: Q = max(Q_АУП, Q_норм) + Σq_ПК.
        var plain = Calculate();
        var r = CalculateWithHydrants(HydrantDemandTests.Parking);
        var h = r.Hydrants!;
        h.Raised.ShouldBeFalse();
        h.JetFlowActual.LitersPerSecond.ShouldBeGreaterThanOrEqualTo(2.6 - 1e-9);
        h.TotalFlow.LitersPerSecond.ShouldBe(2 * h.JetFlowActual.LitersPerSecond, 1e-9);
        r.PumpFlow.LitersPerSecond.ShouldBe(Math.Max(r.DesignFlow.LitersPerSecond, 30) + h.TotalFlow.LitersPerSecond, 1e-9);
        r.PumpPressure.Mpa.ShouldBeGreaterThan(plain.PumpPressure.Mpa);

        // Давление узла = напор у ПК + разность отметок + потери в трубопроводе ПК при фактическом расходе.
        var head = Math.Pow(h.JetFlowActual.LitersPerSecond / h.K, 2);
        (head + h.Elevation.Mpa + h.PipeLoss.Mpa).ShouldBe(r.InletNodePressure.Mpa, 1e-6);
    }

    [Fact]
    public void Hydrants_needing_more_pressure_raise_the_sprinkler_network()
    {
        // Струя 18 м, DN50, спрыск 13, рукав 20 м: 3,6 л/с при 40,6 м; трубопровод ПК DN32 длиной 60 м — больше давления спринклерной сети в узле.
        var s = HydrantDemandTests.Parking with { RoomHeight = new Length(17), NozzleMm = 13, PipeDn = 32, PipeLength = new Length(60) };
        var r = CalculateWithHydrants(s);
        var h = r.Hydrants!;
        h.Raised.ShouldBeTrue();
        h.JetFlowActual.LitersPerSecond.ShouldBe(h.Demand.JetFlow.LitersPerSecond, 1e-6);
        r.InletNodePressure.Mpa.ShouldBe(h.RequiredNodePressure.Mpa, 1e-6);
        r.SprinklerFlows[r.DictatingSprinklerId].LitersPerSecond.ShouldBeGreaterThan(r.DictatingFlow.LitersPerSecond);
        (h.DictatingPressureActual.Mpa + r.Path.Sum(x => x.LossWithLocal.Mpa)).ShouldBe(r.InletNodePressure.Mpa, 1e-6);

        // Напор у ПК 40,6 м = 0,406 МПа > 0,4 МПа — диафрагма или регулятор давления (примечание к 7.11).
        h.ValvePressure.Mpa.ShouldBe(0.406, 1e-6);
        r.Issues.ShouldContain(i => i.Code == "vpv.diaphragm" && i.Clause == "7.11");
    }

    private static DesignResult CalculateRing(bool valvesClosed)
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var layout = SprinklerLayouter.Layout(plan, Rules);
        var route = PipeRouter.Route(plan, layout, Norms, Rules, new Pressure(0.3), ring: true).Routes[0];
        route.Loops.ShouldNotBeEmpty();
        return DesignCalculation.Run(route, Norms, Rules, Settings with { RingValvesClosed = valvesClosed });
    }

    [Fact]
    public void Ring_takes_the_heavier_of_the_loop_solver_and_B_1_2_16_1()
    {
        var r = CalculateRing(valvesClosed: false);
        var ring = r.Ring.ShouldNotBeNull();
        ring.SharePercent.ShouldBe(50);
        r.InletNodePressure.Mpa.ShouldBe(Math.Max(ring.SolverInletPressure.Mpa, ring.NormativeInletPressure.Mpa), 1e-9);
        ring.NormativeTaken.ShouldBe(ring.NormativeInletPressure.Mpa > ring.SolverInletPressure.Mpa);
        r.NormIds.ShouldContain("sp485.b1_2_16_1.ringShare");

        // Нормально закрытые разделительные устройства — 100 % на полуветвь: напор не меньше, чем при 50 %.
        var closed = CalculateRing(valvesClosed: true);
        closed.Ring!.SharePercent.ShouldBe(100);
        closed.Ring.NormativeInletPressure.Mpa.ShouldBeGreaterThanOrEqualTo(ring.NormativeInletPressure.Mpa - 1e-9);
    }

    [Fact]
    public void Worst_zone_is_chosen_by_pump_pressure_among_branch_ends()
    {
        var r = Calculate();
        r.Candidates.Count.ShouldBeGreaterThan(1);
        r.Candidates[0].SprinklerId.ShouldBe(r.DictatingSprinklerId);
        r.Candidates[0].PumpPressure.Mpa.ShouldBe(r.PumpPressure.Mpa, 1e-12);
        r.Candidates.Select(c => c.PumpPressure.Mpa).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public void A_nearer_branch_end_with_a_thin_pipe_becomes_dictating()
    {
        // Ветвь у ввода с трубой DN15 даёт большее давление насоса, чем самый удалённый ороситель.
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var layout = SprinklerLayouter.Layout(plan, Rules);
        var route = PipeRouter.Route(plan, layout, Norms, Rules, new Pressure(0.3)).Routes[0];
        var farthest = DesignCalculation.Run(route, Norms, Rules, Settings).DictatingSprinklerId;
        var ends = route.Nodes.Where(x => x.Kind == RouteNodeKind.Sprinkler && route.Segments.All(s => s.From != x.Id)).Select(x => x.Id).ToList();
        var near = ends.First(x => x != farthest);
        var parents = route.Segments.ToDictionary(s => s.To);
        var thin = new HashSet<string>();
        for (var id = near; parents.TryGetValue(id, out var s) && route.Nodes.First(x => x.Id == s.From).Kind == RouteNodeKind.Sprinkler; id = s.From)
        {
            thin.Add(s.Id);
        }

        thin.Add(parents[near].Id);
        var changed = route with { Segments = route.Segments.Select(s => thin.Contains(s.Id) ? s with { Dn = 15 } : s).ToList() };
        DesignCalculation.Run(changed, Norms, Rules, Settings).DictatingSprinklerId.ShouldBe(near);
    }

    [Fact]
    public void Dictating_sprinkler_is_the_lowest_pressure_one_of_its_zone()
    {
        // Б.1.2.1: диктующий — ороситель зоны с наименьшим давлением. Если у оросителя зоны давление ниже, чем у принятого
        // диктующего, та же зона пересчитывается с ним диктующим — замечания «диктующий выбран неверно» в результате нет,
        // а давление насоса не меньше, чем при неверном диктующем.
        foreach (var velocity in new double?[] { null, 5 })
        {
            var r = Calculate(velocity);
            r.Issues.ShouldNotContain(i => i.Code == "dictating.notMinimum", string.Join("; ", r.Issues.Where(i => i.Code == "dictating.notMinimum").Select(i => i.Message)));
            r.ZoneSprinklers.ShouldContain(r.DictatingSprinklerId);
        }
    }

    [Fact]
    public void Dictating_flow_and_pressure_follow_B1_with_the_upper_factor()
    {
        // q = 1,5 · 0,12 · 12 = 2,16 л/с; p = (q / 10K)² = (2,16 / 4,7)² = 0,2112 МПа.
        var r = Calculate();
        r.DictatingFactor.ShouldBe(1.5);
        r.DictatingFlow.LitersPerSecond.ShouldBe(2.16, 1e-9);
        r.DictatingPressure.Mpa.ShouldBe(Math.Pow(2.16 / 4.7, 2), 1e-9);
    }

    [Fact]
    public void Zone_size_follows_B2_B3()
    {
        // Сетка ≈ 3,33 × 3,33 м: Q_ор ≈ 11,1 м², N = ⌈120 / 11,1⌉ = 11.
        var r = Calculate();
        r.AreaPerSprinkler.ShouldBe(r.RowSpacing.Meters * r.LineSpacing.Meters, 1e-9);
        r.ZoneSprinklers.Count.ShouldBe((int)Math.Ceiling(120 / r.AreaPerSprinkler));
        r.ZoneSprinklers.ShouldContain(r.DictatingSprinklerId);
    }

    [Fact]
    public void Flow_is_the_sum_of_zone_sprinklers_and_pump_flow_follows_B24_B26()
    {
        var r = Calculate();
        r.DesignFlow.LitersPerSecond.ShouldBe(r.SprinklerFlows.Values.Sum(f => f.LitersPerSecond), 1e-9);
        r.SprinklerFlows.Count.ShouldBe(r.ZoneSprinklers.Count);
        r.SprinklerFlows.Values.ShouldAllBe(f => f.LitersPerSecond >= 2.16 - 1e-9);
        r.NormativeFlow.LitersPerSecond.ShouldBe(30);
        r.PumpFlow.LitersPerSecond.ShouldBe(Math.Max(r.DesignFlow.LitersPerSecond, 30));
        r.PumpFlowClause.ShouldBe(r.DesignFlow.LitersPerSecond <= 30 ? "Б.24" : "Б.26");
    }

    [Fact]
    public void Path_losses_include_20_percent_and_add_up_to_the_inlet_pressure()
    {
        var r = Calculate();
        r.LocalLossFactor.ShouldBe(0.2);
        r.Path.ShouldAllBe(s => Math.Abs(s.LossWithLocal.Mpa - (1.2 * s.LinearLoss.Mpa)) < 1e-12);
        (r.DictatingPressure.Mpa + r.Path.Sum(s => s.LossWithLocal.Mpa)).ShouldBe(r.InletNodePressure.Mpa, 1e-9);
        r.Path[0].From.ShouldBe("I1");
        r.Path[^1].To.ShouldBe(r.DictatingSprinklerId);
    }

    [Fact]
    public void Pump_pressure_follows_B30()
    {
        var r = Calculate();
        var supplyLinear = Math.Pow(r.PumpFlow.LitersPerSecond, 2) * 25.0 / (100 * 5205); // DN100 ГОСТ 3262: Kт = 5205
        r.SupplyLinearLoss.Mpa.ShouldBe(supplyLinear, 1e-9);
        r.Elevation.Mpa.ShouldBe(0.04, 1e-12); // Z = H / 100
        r.PumpPressure.Mpa.ShouldBe(r.InletNodePressure.Mpa + (1.2 * supplyLinear) + 0.04 - 0.10, 1e-9);
        r.PumpHead.Meters.ShouldBe(r.PumpPressure.Mpa * 100, 1e-9);
    }

    [Fact]
    public void Velocities_are_checked_against_6_7_1_37()
    {
        var r = Calculate();
        r.VelocityLimit.ShouldBe(10);
        r.Path.ShouldAllBe(s => s.Velocity > 0);
        r.SupplyVelocity.ShouldBe(r.PumpFlow.LitersPerSecond / 1000 / (Math.PI * Math.Pow(0.106, 2) / 4), 1e-9); // Ø114×4 → 106 мм
        r.Issues.Any(i => i.Code == "design.velocity").ShouldBe(r.Path.Any(s => s.Velocity > 10) || r.SupplyVelocity > 10);
    }

    [Fact]
    public void Diameters_are_sized_by_B5_for_the_chosen_velocity()
    {
        // Б.5: d = 1000·√(4Q / (π·μ·V)), μ = 0,90; после подбора скорость на участках с расходом не больше V.
        var plain = Calculate();
        var sized = Calculate(velocity: 5);
        sized.DischargeCoefficient.ShouldBe(0.9);
        sized.FirstSegmentDiameterMm.ShouldBe(1000 * Math.Sqrt(4 * 2.16 / 1000 / (Math.PI * 0.9 * 5)), 1e-9);
        sized.Path.ShouldAllBe(s => s.Velocity <= 5 + 1e-9);
        sized.PumpPressure.Mpa.ShouldBeLessThan(plain.PumpPressure.Mpa);

        // Выше по течению диаметр не меньше, чем за участком.
        var byTo = sized.Route.Segments.ToDictionary(s => s.To);
        sized.Route.Segments.Where(s => byTo.ContainsKey(s.From)).ShouldAllBe(s => s.Dn <= byTo[s.From].Dn);
    }

    [Fact]
    public void Warehouse_parameters_drive_the_calculation()
    {
        // Склад группы 5, складирование 3,5 м, помещение 14 м: i = 0,384, Q_н = 72 л/с (табл. 6.2, прим. 3), S = 90 м².
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Склад", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        var rules = LayoutRules.Resolve(Norms, "5", ConstructionClass.K0).Rules!;
        var layout = SprinklerLayouter.Layout(plan, rules);
        var route = PipeRouter.Route(plan, layout, Norms, rules, new Pressure(0.3)).Routes[0];
        var parameters = FireParameters.Resolve(Norms, "5", new Length(14), new Length(3.5)).Parameters!;
        var r = DesignCalculation.Run(route, Norms, rules, Settings with { DesignVelocity = 5 }, parameters);

        r.Parameters.ShouldBe(parameters);
        r.DictatingFlow.LitersPerSecond.ShouldBe(1.5 * 0.384 * 12, 1e-9);
        r.NormativeFlow.LitersPerSecond.ShouldBe(72, 1e-9);
        r.ZoneSprinklers.Count.ShouldBe((int)Math.Ceiling(90 / r.AreaPerSprinkler));
        r.PumpFlow.LitersPerSecond.ShouldBeGreaterThanOrEqualTo(72);
    }

    [Fact]
    public void Tank_volume_is_flow_times_duration_with_95_percent_fill()
    {
        var r = Calculate();
        r.Duration.ShouldBe(60);
        r.WaterVolume.ShouldBe(r.PumpFlow.LitersPerSecond * 60 * 60 / 1000, 1e-9);
        r.TankVolume.ShouldBe(r.WaterVolume / 0.95, 1e-9);
        r.JockeyTankMinLiters.ShouldBe(40);
    }
}
