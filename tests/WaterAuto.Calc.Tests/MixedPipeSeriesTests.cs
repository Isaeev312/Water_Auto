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
}
