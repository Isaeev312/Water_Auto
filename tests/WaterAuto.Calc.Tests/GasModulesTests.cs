using WaterAuto.Calc.Gas;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

// Число модулей АУГП: масса Mг по Д.1 с остатком Mб·n, наполнение модуля не больше максимального по ТД и не меньше 44 % (9.6.5).
public class GasModulesTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static readonly GasRoom Room = new("R1", "Серверная", 180, 3, "sp485.tG.hfc227ea", "heptane", 0.01, GasOpenings.Other, 20, 800);

    private static GasModuleModel Module(double volume, double? residue = 0.5) =>
        new("test.m", "МГП-тест", volume, new Dictionary<string, GasModuleCharge> { ["hfc227ea"] = new(1.1, residue) }, "проба");

    [Fact]
    public void Module_count_iterates_with_residue_in_D1()
    {
        // Mр = 101,71 кг; 40 л × 1,1 = 44 кг в модуле. n = 1: 1,05·(101,71 + 0,5) = 107,3 → 3; n = 3: 1,05·(101,71 + 1,5) = 108,4 → 3.
        var r = GasModules.Select(Room, Module(40), Norms);

        r.Modules.ShouldBe(3);
        r.Mass.TotalMass.ShouldBe(1.05 * (r.Mass.DesignMass + 1.5), 1e-9);
        r.MassPerModule.ShouldBe(r.Mass.TotalMass / 3, 1e-9);
        r.FillPercent.ShouldBe(r.MassPerModule / 44 * 100, 1e-9);
        r.Issues.ShouldNotContain(i => i.Code == "gas.module.fill");
    }

    [Fact]
    public void Underfilled_module_unknown_agent_and_missing_residue_are_reported()
    {
        // 1000 л: 108 кг из 1100 — 10 % < 44 % (9.6.5).
        GasModules.Select(Room, Module(1000), Norms).Issues.ShouldContain(i => i.Code == "gas.module.fill" && i.Level == IssueLevel.Error);
        GasModules.Select(Room with { AgentId = "sp485.tG.co2" }, Module(40), Norms).Issues.ShouldContain(i => i.Code == "gas.module.agent" && i.Level == IssueLevel.Error);
        GasModules.Select(Room, Module(40, null), Norms).Issues.ShouldContain(i => i.Code == "gas.module.noResidue");
    }
}
