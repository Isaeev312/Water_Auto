using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class SprinklerSelectionTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);
    private static readonly LayoutRules Rules = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0).Rules!;
    private static readonly DesignSettings Settings = new("ГОСТ 3262", new KFactor(1), new Length(4), new Length(25), 100, new Pressure(0.1), 5);

    private static SprinklerCandidate C(string id, double k, double pMin = 0.05, double pMax = 1.0, double area = 12, params (double P, double I)[] i) =>
        new(id, "Изготовитель", id, k, pMin, pMax, area, i.Length > 0 ? i : [(0.1, 0.15), (0.3, 0.25)]);

    private static IReadOnlyList<PipeRoute> Routes()
    {
        var contour = new Polygon([new(0, 0), new(30000, 0), new(30000, 20000), new(0, 20000)]);
        var plan = new FloorPlan([new Room("R1", "Автостоянка", contour)], [], [], [], [], [new Inlet("I1", new(500, 500), null)]);
        return PipeRouter.Route(plan, SprinklerLayouter.Layout(plan, Rules), Norms, Rules, new Pressure(0.3)).Routes;
    }

    [Fact]
    public void Chooses_the_valid_model_with_the_least_pump_power()
    {
        var r = SprinklerSelection.Select(Routes(), Norms, Rules, Settings, null, null, [C("A", 0.47), C("B", 0.6), C("C", 0.77)]);
        r.Chosen.ShouldNotBeNull();
        var valid = r.Rows.Where(x => x.Valid).ToList();
        valid.ShouldContain(x => x.Candidate.Id == r.Chosen!.Id);
        var chosen = valid.Single(x => x.Candidate.Id == r.Chosen!.Id);
        valid.ShouldAllBe(x => x.Power >= chosen.Power - 1e-9);
        r.Design!.DictatingPressure.Mpa.ShouldBe(Math.Pow(2.16 / (10 * r.Chosen!.K), 2), 1e-9);
    }

    [Fact]
    public void Model_outside_its_pressure_range_or_area_is_rejected()
    {
        // K = 0,35: p₁ = (2,16/3,5)² = 0,38 МПа > pMax 0,3; площадь 5 м² < l·b.
        var r = SprinklerSelection.Select(Routes(), Norms, Rules, Settings, null, null, [C("HighP", 0.35, pMax: 0.3), C("Small", 0.6, area: 5), C("Ok", 0.6)]);
        r.Rows.Single(x => x.Candidate.Id == "HighP").Valid.ShouldBeFalse();
        r.Rows.Single(x => x.Candidate.Id == "Small").Valid.ShouldBeFalse();
        r.Chosen!.Id.ShouldBe("Ok");
    }

    [Fact]
    public void Catalog_intensity_below_the_norm_rejects_the_model()
    {
        // i по каталогу 0,05…0,08 < 0,12 л/(с·м²) при любом давлении из диапазона.
        var r = SprinklerSelection.Select(Routes(), Norms, Rules, Settings, null, null, [C("Weak", 0.6, i: [(0.1, 0.05), (0.3, 0.08)])]);
        r.Chosen.ShouldBeNull();
        r.Issues.ShouldContain(i => i.Code == "sprinkler.noneSuitable" && i.Level == IssueLevel.Error);
    }
}
