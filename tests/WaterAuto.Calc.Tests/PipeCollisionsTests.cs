using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

public class PipeCollisionsTests
{
    private static readonly PipeSegment3 Network = new(new(0, 5000), new(10000, 5000), 3.1, 159);

    [Fact]
    public void Crossing_pipes_100_mm_apart_collide_when_diameters_need_more()
    {
        // DN150 и DN150: нужно 159 + 20 = 179 мм, есть 100 мм.
        var issues = PipeCollisions.Check([new(new(5000, 0), new(5000, 10000), 3.0, 159)], [Network], 20, "ввод и сеть АУП");
        var i = issues.ShouldHaveSingleItem();
        i.Code.ShouldBe("pipe.collision");
        i.Location.ShouldBe(new PlanPoint(5000, 5000));
    }

    [Fact]
    public void Max_required_gap_is_the_tightest_crossing()
    {
        PipeCollisions.MaxRequiredGap([new(new(5000, 0), new(5000, 10000), 0, 159)], [Network], 20).ShouldBe(179, 1e-9);
        PipeCollisions.MaxRequiredGap([new(new(0, 6000), new(10000, 6000), 0, 159)], [Network], 20).ShouldBe(0);
    }

    [Fact]
    public void Enough_vertical_gap_or_no_crossing_is_fine()
    {
        PipeCollisions.Check([new(new(5000, 0), new(5000, 10000), 2.9, 159)], [Network], 20, "ввод").ShouldBeEmpty();
        PipeCollisions.Check([new(new(0, 6000), new(10000, 6000), 3.1, 159)], [Network], 20, "ввод").ShouldBeEmpty();
    }
}
