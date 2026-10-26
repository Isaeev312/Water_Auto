using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

public class ReservoirCheckTests
{
    private static UserTank Tank(double l, double w, double h, double dead = 0) =>
        new(new Length(l), new Length(w), new Length(h), new Length(dead));

    [Fact]
    public void Useful_volume_excludes_the_dead_layer()
    {
        // 4 × 3 × 2,5 м, мёртвый слой 0,5 м: полезный объём = 4 · 3 · (2,5 − 0,5) = 24 м³.
        Tank(4, 3, 2.5, 0.5).UsefulVolumeM3.ShouldBe(24, 1e-9);
    }

    [Fact]
    public void Tank_that_holds_the_required_reserve_is_not_an_issue()
    {
        // Полезный объём 24 м³ × 95 % = 22,8 м³ ≥ 20 м³ — достаточно.
        var r = ReservoirCheck.Check(Tank(4, 3, 2.5, 0.5), requiredWaterM3: 20, maxFillPercent: 95);
        r.ShouldBeNull();
    }

    [Fact]
    public void Tank_too_small_for_the_reserve_is_an_issue()
    {
        // 22,8 м³ < 25 м³ — запас не помещается.
        var r = ReservoirCheck.Check(Tank(4, 3, 2.5, 0.5), requiredWaterM3: 25, maxFillPercent: 95);
        r.ShouldNotBeNull();
        r!.Code.ShouldBe("reservoir.insufficient");
        r.Level.ShouldBe(IssueLevel.Warning);
        r.Clause.ShouldBe("6.9.13");
    }

    [Fact]
    public void No_user_tank_is_not_an_issue()
    {
        ReservoirCheck.Check(null, requiredWaterM3: 20, maxFillPercent: 95).ShouldBeNull();
    }
}
