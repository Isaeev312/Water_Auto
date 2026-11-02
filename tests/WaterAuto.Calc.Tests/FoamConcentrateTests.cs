using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

// Пенообразователь пенной АУП (пп. 6.9.18, 6.9.19, 6.9.23 СП 485): раствор на время тушения и трубы, концентрат по паспорту, 100 % резерв.
public class FoamConcentrateTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    [Fact]
    public void Concentrate_is_solution_times_concentration_plus_full_reserve()
    {
        // Q = 30 л/с, t = 60 мин: 108 м³; трубы 2 м³ → 110 м³ раствора; 6 % → 6,6 м³ пенообразователя, резерв 6,6 м³, всего 13,2 м³.
        // Дозирование: от 1 оросителя (1,2 л/с) до 30 л/с.
        var r = FoamConcentrate.Calculate(new Flow(30), 60, 2, 6, new Flow(1.2), Norms);

        r.SolutionM3.ShouldBe(110, 1e-9);
        r.ConcentrateM3.ShouldBe(6.6, 1e-9);
        r.ReserveM3.ShouldBe(6.6, 1e-9);
        r.TotalM3.ShouldBe(13.2, 1e-9);
        r.DosingMin.LitersPerSecond.ShouldBe(1.2);
        r.DosingMax.LitersPerSecond.ShouldBe(30);
    }

    [Fact]
    public void Concentration_must_come_from_the_passport()
    {
        FoamConcentrate.Calculate(new Flow(30), 60, 2, 0, new Flow(1.2), Norms).Issues.ShouldContain(i => i.Code == "foam.concentration" && i.Level == IssueLevel.Error);
    }
}
