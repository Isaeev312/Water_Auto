using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

// Воздухозаполненная АУП (пп. 6.2.4, 6.2.6, 6.2.19, 6.7.1.21 СП 485): объём воздуха в сети, положение оросителей, уклоны.
public class AirFilledTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static PipeRoute Route() => new("R1", "IN", [], [
        new RouteSegment("P1", "IN", "A", RouteSegmentRole.Supply, new Length(10), 10, 50),
        new RouteSegment("P2", "A", "B", RouteSegmentRole.Row, new Length(20), 4, 25),
    ]);

    [Fact]
    public void Air_volume_is_the_inner_volume_of_the_pipes_and_slopes_follow_the_dn()
    {
        // ГОСТ 3262: DN50 — 60,0 × 3,5 → d = 53 мм; DN25 — 33,5 × 3,2 → d = 27,1 мм. V = π/4·(0,053²·10 + 0,0271²·20).
        var series = new PipeSeries(Norms, "ГОСТ 3262-75");
        var r = AirFilled.Check([Route()], series, "up", 2, Norms);

        var d50 = (series.Pick(50)!.OuterDiameterMm - (2 * series.Pick(50)!.WallMm)) / 1000;
        var d25 = (series.Pick(25)!.OuterDiameterMm - (2 * series.Pick(25)!.WallMm)) / 1000;
        r.AirVolumeM3.ShouldBe(Math.PI / 4 * ((d50 * d50 * 10) + (d25 * d25 * 20)), 1e-9);
        r.SlopeSmall.ShouldBe(0.01);
        r.SlopeLarge.ShouldBe(0.005);
        r.Issues.ShouldContain(i => i.Code == "air.delay" && i.Level == IssueLevel.Warning);
        r.Issues.ShouldNotContain(i => i.Code == "air.deflector");
    }

    [Fact]
    public void Pendent_sprinklers_below_5_degrees_are_an_error()
    {
        var series = new PipeSeries(Norms, "ГОСТ 3262-75");
        AirFilled.Check([Route()], series, "down", -10, Norms).Issues.ShouldContain(i => i.Code == "air.deflector" && i.Level == IssueLevel.Error);
        AirFilled.Check([Route()], series, "down", 10, Norms).Issues.ShouldNotContain(i => i.Code == "air.deflector");
    }
}
