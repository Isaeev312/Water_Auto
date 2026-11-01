using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class JockeyTankTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static readonly MembraneTank[] Catalog =
    [
        new("Бак 24 л PN 10", 24, 10, 2, "паспорт"),
        new("Бак 50 л PN 10", 50, 10, 2, "паспорт"),
        new("Бак 80 л PN 10", 80, 10, 2, "паспорт"),
        new("Бак 50 л PN 16", 50, 16, 2, "паспорт"),
    ];

    [Fact]
    public void Smallest_tank_not_less_than_40_l_with_enough_PN_is_chosen()
    {
        // п. 6.9.4: не менее 40 л → 50 л; давление 0,62 МПа = 6,2 бар ≤ PN 10.
        var r = JockeyTank.Select(Catalog, Norms, new Pressure(0.62), stop: null, start: null);
        r.Tank!.Designation.ShouldBe("Бак 50 л PN 10");
        r.NormIds.ShouldContain("sp485.p6_9_4.jockeyTank");
    }

    [Fact]
    public void Higher_pressure_takes_a_PN16_tank_and_none_above_it_is_an_issue()
    {
        JockeyTank.Select(Catalog, Norms, new Pressure(1.2), null, null).Tank!.Designation.ShouldBe("Бак 50 л PN 16");
        var none = JockeyTank.Select(Catalog, Norms, new Pressure(1.8), null, null);
        none.Tank.ShouldBeNull();
        none.Issues.ShouldContain(i => i.Code == "jockey.tankNone" && i.Level == IssueLevel.Warning);
    }

    [Fact]
    public void Air_precharge_gives_50_to_60_percent_water_at_the_stop_pressure()
    {
        // Бойль — Мариотт по абсолютным давлениям: доля воды f = 1 − (p0 + pa) / (p + pa); берётся середина 50–60 % → 55 %.
        // p = 0,60 МПа: p0 = 0,45·(0,60 + 0,101325) − 0,101325 = 0,2143 МПа.
        var r = JockeyTank.Select(Catalog, Norms, new Pressure(0.62), stop: new Pressure(0.60), start: new Pressure(0.50));
        r.Precharge!.Value.Mpa.ShouldBe(0.2143, 0.0005);
        r.WaterShare!.Value.ShouldBe(0.55, 1e-9);
        r.Issues.ShouldNotContain(i => i.Level == IssueLevel.Warning);
    }
}
