using Shouldly;
using WaterAuto.Calc.Hydraulics;

namespace WaterAuto.Application.Tests;

/// <summary>Подбор установки для интерфейса: те же правила, что в автоподборе расчёта, и кривые для графика.</summary>
public sealed class PumpAdvisorTests
{
    [Fact]
    public void Candidates_are_sorted_like_automatic_selection()
    {
        var sets = PumpCatalog.Sets(RepoPaths.Norms);
        var auto = PumpSetSelection.Choose(sets, 108, 30);
        auto.Chosen.ShouldNotBeNull();

        var candidates = PumpAdvisor.Candidates(RepoPaths.Norms, 108, 30);

        candidates.Count.ShouldBe(sets.Count);
        candidates.ShouldAllBe(c => c.Curve.Count > 1);
        candidates[0].Fits.ShouldBeTrue();
        candidates[0].Designation.ShouldBe(auto.Chosen!.Designation);
        candidates.Where(c => c.Fits).ShouldAllBe(c => c.MarginMeters >= -1e-9);
        candidates.ShouldContain(c => !c.Fits);
    }

    [Fact]
    public void Flow_outside_all_curves_has_no_fitting_candidates()
    {
        var candidates = PumpAdvisor.Candidates(RepoPaths.Norms, 100_000, 30);

        candidates.ShouldNotBeEmpty();
        candidates.ShouldAllBe(c => !c.Fits && c.HeadAtDutyMeters == null);
    }
}
