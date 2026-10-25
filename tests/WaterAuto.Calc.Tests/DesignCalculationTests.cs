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
