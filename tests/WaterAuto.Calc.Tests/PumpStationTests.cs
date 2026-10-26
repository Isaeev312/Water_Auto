using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class PumpStationTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static PumpStationResult Design(double qLs, double inletMpa = 0.1) =>
        PumpStation.Design(Norms, new Flow(qLs), new Length(60), new PipeSeries(Norms, "ГОСТ 10704"), new Pressure(inletMpa));

    [Fact]
    public void One_working_and_one_standby_pump_6_10_3()
    {
        var r = Design(30);
        r.WorkingPumps.ShouldBe(1);
        r.StandbyPumps.ShouldBe(1);
    }

    [Fact]
    public void Suction_pipe_keeps_velocity_within_6_7_1_37()
    {
        // Подпор на входе — до 2,8 м/с; без подпора (P_вх = 0) — до 1,5 м/с, труба больше.
        var positive = Design(20);
        positive.Suction.Velocity.ShouldBeLessThanOrEqualTo(2.8);
        positive.SuctionLimit.ShouldBe(2.8);
        var negative = Design(20, 0);
        negative.SuctionLimit.ShouldBe(1.5);
        negative.Suction.Velocity.ShouldBeLessThanOrEqualTo(1.5);
        negative.Suction.Pipe.Dn.ShouldBeGreaterThan(positive.Suction.Pipe.Dn);

        // Наименьшая подходящая: на DN меньше скорость уже выше предела.
        var inner = (positive.Suction.Pipe.OuterDiameterMm - (2 * positive.Suction.Pipe.WallMm)) / 1000;
        (0.020 / (Math.PI * inner * inner / 4)).ShouldBe(positive.Suction.Velocity, 1e-9);
    }

    [Fact]
    public void Too_large_pipe_is_an_issue_not_an_exception()
    {
        // 30 л/с без подпора — внутренний диаметр ≈ 160 мм, больше DN 150 сортамента.
        Design(30, 0).Issues.ShouldContain(i => i.Code == "pump.pipeTooLarge");
    }

    [Fact]
    public void Discharge_pipe_is_not_smaller_than_suction_side_rule_and_within_10_m_s()
    {
        var r = Design(30);
        r.Discharge.Velocity.ShouldBeLessThanOrEqualTo(10);
        r.Discharge.Pipe.Dn.ShouldBeLessThanOrEqualTo(r.Suction.Pipe.Dn);
    }

    [Fact]
    public void Two_truck_inlets_DN80_6_10_17()
    {
        var r = Design(30);
        r.TruckInlets.ShouldBe(2);
        r.TruckInletDn.ShouldBe(80);
    }
}
