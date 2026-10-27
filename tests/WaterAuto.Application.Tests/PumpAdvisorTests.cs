using Shouldly;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

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
    public void Candidates_carry_footprints_and_collector_axis_from_catalog()
    {
        var sets = PumpCatalog.Sets(RepoPaths.Norms);
        sets.ShouldNotBeEmpty();
        var catalog = sets.ToDictionary(s => s.Designation, StringComparer.Ordinal);

        var candidates = PumpAdvisor.Candidates(RepoPaths.Norms, 108, 30);

        candidates.Count.ShouldBe(catalog.Count);
        candidates.ShouldAllBe(c => HasPositiveFootprint(c.Hydraulic) && HasPositiveFootprint(c.Cabinet));
        candidates.ShouldAllBe(c => c.CollectorAxisMm > 0);

        // Габариты — данные каталога, без пересчёта: сверяем пару установок по обозначению.
        foreach (var candidate in new[] { candidates[0], candidates[candidates.Count - 1] })
        {
            var set = catalog[candidate.Designation];
            candidate.Hydraulic.ShouldBe(set.Hydraulic);
            candidate.Cabinet.ShouldBe(set.Cabinet);
            candidate.CollectorAxisMm.ShouldBe(set.CollectorAxisMm);
        }
    }

    [Fact]
    public void Flow_outside_all_curves_has_no_fitting_candidates()
    {
        var candidates = PumpAdvisor.Candidates(RepoPaths.Norms, 100_000, 30);

        candidates.ShouldNotBeEmpty();
        candidates.ShouldAllBe(c => !c.Fits && c.HeadAtDutyMeters == null);
    }

    private static bool HasPositiveFootprint(Footprint footprint) =>
        footprint.LengthMm > 0 && footprint.WidthMm > 0 && footprint.HeightMm > 0;
}
