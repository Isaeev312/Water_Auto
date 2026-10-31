using WaterAuto.Calc.Gas;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

// Масса ГОТВ по прил. Д СП 485 (Д.1–Д.9) и площадь проёма сброса давления по прил. Ж (Ж.1); ожидаемые значения — вручную.
public class GasMassTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static GasRoom Room(string agent = "sp485.tG.hfc227ea", string material = "heptane", double open = 0.01, double altitude = 800, double minT = 20, GasClassA1 a1 = GasClassA1.None, double? overpressure = null) =>
        new("R1", "Серверная", 180, 3, agent, material, open, GasOpenings.Other, minT, altitude, ClassA1: a1, MaxOverpressureMPa: overpressure);

    [Fact]
    public void Liquefied_agent_mass_follows_D2_and_D1()
    {
        // δ = 0,01/180; K2 = 0,4·δ·10·√3 = 3,85e-4; ρ1 = 7,28 (20 °С, K3 = 1); Mр = 180·7,28·(1+K2)·7,2/92,8 = 101,71; Mг = 1,05·Mр = 106,80.
        var r = GasMass.Calculate(Room(), Norms);

        r.Concentration.ShouldBe(7.2);
        r.Rho1.ShouldBe(7.28, 1e-9);
        r.Delta.ShouldBe(0.01 / 180, 1e-12);
        r.DischargeTimeS.ShouldBe(10);
        r.K2.ShouldBe(0.4 * (0.01 / 180) * 10 * Math.Sqrt(3), 1e-12);
        r.DesignMass.ShouldBe(180 * 7.28 * (1 + r.K2) * 7.2 / 92.8, 1e-9);
        r.TotalMass.ShouldBe(1.05 * r.DesignMass, 1e-9);
        r.Issues.ShouldContain(i => i.Code == "gas.pipeResidue");
    }

    [Fact]
    public void Co2_uses_the_logarithmic_formula_and_altitude_and_temperature_change_density()
    {
        // CO₂, 1700 м (K3 = 0,83), Tм = 5 °С: ρ1 = 1,88·293/278·0,83; Mр = 180·ρ1·(1+K2)·ln(100/65,1); время подачи 60 с.
        var r = GasMass.Calculate(Room("sp485.tG.co2", altitude: 1700, minT: 5), Norms);

        r.K3.ShouldBe(0.83);
        r.DischargeTimeS.ShouldBe(60);
        r.Rho1.ShouldBe(1.88 * 293 / 278.0 * 0.83, 1e-9);
        r.DesignMass.ShouldBe(180 * r.Rho1 * (1 + r.K2) * Math.Log(100 / (100 - 34.9)), 1e-9);
    }

    [Fact]
    public void Leakage_above_table_G16_and_unknown_material_are_errors()
    {
        // 180 м³ → не более 0,006 м⁻¹; 2 м² открытых проёмов дают 0,011 — тушение объёмным способом недопустимо.
        GasMass.Calculate(Room(open: 2), Norms).Issues.ShouldContain(i => i.Code == "gas.leakage" && i.Level == IssueLevel.Error);
        GasMass.Calculate(Room(material: "diesel"), Norms).Issues.ShouldContain(i => i.Code == "gas.concentration.missing" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Class_A1_multiplies_the_heptane_mass_and_requires_a_tight_room()
    {
        var plain = GasMass.Calculate(Room(open: 0.1), Norms);
        var paper = GasMass.Calculate(Room(open: 0.1, a1: GasClassA1.PaperBales), Norms);
        paper.DesignMass.ShouldBe(1.3 * plain.DesignMass, 1e-9);
        // δ = 0,1/180 = 5,6e-4 ≤ 0,001 — допустимо; 0,5 м² — 2,8e-3 > 0,001: ошибка Д.3.
        paper.Issues.ShouldNotContain(i => i.Code == "gas.a1.leakage");
        GasMass.Calculate(Room(open: 0.5, a1: GasClassA1.Other), Norms).Issues.ShouldContain(i => i.Code == "gas.a1.leakage" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Relief_area_follows_Zh1_and_is_zero_when_existing_openings_suffice()
    {
        // Ж.1: Fс = 1,2·1·Mр/(0,7·1,05·10·7,28) · √(1,2 / (7·10⁶·0,1·((0,002+0,1)/0,1)^0,2857 − 1)) − 0,01.
        var r = GasMass.Calculate(Room(overpressure: 0.002), Norms);
        var root = Math.Sqrt(1.2 / (7e6 * 0.1 * (Math.Pow((0.002 + 0.1) / 0.1, 0.2857) - 1)));
        var expected = (1.2 * 1 * r.DesignMass / (0.7 * 1.05 * 10 * 7.28) * root) - 0.01;
        r.ReliefAreaM2!.Value.ShouldBe(expected, 1e-9);

        // Большие постоянные проёмы — правая часть ≤ 0: проём не нужен (0).
        GasMass.Calculate(Room(open: 1, overpressure: 0.002), Norms).ReliefAreaM2.ShouldBe(0);
        GasMass.Calculate(Room(), Norms).ReliefAreaM2.ShouldBeNull();
    }
}
