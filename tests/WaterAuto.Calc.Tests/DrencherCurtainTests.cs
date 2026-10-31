using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

// Дренчерная водяная завеса (пп. 6.3.6, 6.3.10, 6.3.11, Б.1.3.4–Б.1.3.6 СП 485).
public class DrencherCurtainTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);
    private static readonly PipeSeries Series = new(Norms, "ГОСТ 3262-75");

    [Fact]
    public void Narrow_opening_is_one_row_with_1_lps_per_metre()
    {
        // 4 м, K = 0,35, p = 0,1 МПа: q = 10·0,35·√0,1 = 1,107 л/с → шаг ≤ 1,107 м → n = ⌈4 / 1,107⌉ = 4, шаг 1,0 м.
        var r = DrencherCurtain.Calculate(new CurtainInput("C1", "Ворота", 4, 0.35, 0.1, 25, "2"), Series, Norms);

        r.Rows.ShouldBe(1);
        r.PerRow.ShouldBe(4);
        r.SpacingM.ShouldBe(1.0, 1e-9);
        r.TotalFlow.LitersPerSecond.ShouldBeGreaterThan(4 * 10 * 0.35 * Math.Sqrt(0.1));
        (r.TotalFlow.LitersPerSecond / 4).ShouldBeGreaterThanOrEqualTo(1.0);
        r.FeedPressure.Mpa.ShouldBeGreaterThan(0.1);
        r.Duration.ShouldBe(60);
    }

    [Fact]
    public void Wide_opening_is_two_rows_with_ends_within_half_a_metre_of_the_wall()
    {
        // 6 м: две нитки по 0,5 л/(с·м); по расходу шаг ≤ 2,21 м (3 оросителя), но крайние — не дальше 0,5 м от стены: шаг ≤ 1 м → 6 на нитку.
        var r = DrencherCurtain.Calculate(new CurtainInput("C2", "Проём", 6, 0.35, 0.1, 32, "1"), Series, Norms);

        r.Rows.ShouldBe(2);
        r.PerRow.ShouldBe(6);
        r.SpacingM.ShouldBe(1.0, 1e-9);
        r.Duration.ShouldBe(30);
        r.TotalFlow.LitersPerSecond.ShouldBeGreaterThan(12 * 10 * 0.35 * Math.Sqrt(0.1));
    }
}
