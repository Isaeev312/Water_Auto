using WaterAuto.Calc.Routing;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class MixedPipeSeriesTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    [Fact]
    public void Small_pipes_by_GOST_3262_and_large_by_GOST_10704()
    {
        var standard = PipeSeries.Compose("ГОСТ 3262-75", "ГОСТ 10704-91", 50);
        var series = new PipeSeries(Norms, standard);
        series.Pick(40)!.Standard.ShouldBe("ГОСТ 3262-75");
        series.Pick(25)!.Standard.ShouldBe("ГОСТ 3262-75");
        series.Pick(50)!.Standard.ShouldBe("ГОСТ 10704-91");
        series.Pick(100)!.Standard.ShouldBe("ГОСТ 10704-91");
    }

    [Fact]
    public void Designation_with_year_matches_the_table_standard()
    {
        new PipeSeries(Norms, "ГОСТ 3262-75").Pick(32)!.Standard.ShouldBe("ГОСТ 3262-75");
    }

    [Fact]
    public void Accepted_size_replaces_the_table_row_and_gets_Kt_by_B8()
    {
        var series = new PipeSeries(Norms, "ГОСТ 10704-91", [new PipeSize("ГОСТ 10704", 50, 57, 3.5)]);
        var pipe = series.Pick(50)!;
        pipe.OuterDiameterMm.ShouldBe(57);
        pipe.WallMm.ShouldBe(3.5);
        pipe.NormId.ShouldStartWith("sp485.tB_3");

        // Ø50 мм внутри меньше, чем у 57×2,5 (Ø52) — Kт меньше табличного 110.
        pipe.Kt.Kt.ShouldBeLessThan(110);
        pipe.Kt.Kt.ShouldBeGreaterThan(50);
    }
}
