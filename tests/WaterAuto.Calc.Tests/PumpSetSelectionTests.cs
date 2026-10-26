using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

public class PumpSetSelectionTests
{
    private static PumpSet Set(string name, int main, double kw, double h0, double qMax) =>
        new(name, name, main, 1, kw, [(0, h0), (qMax / 2, h0 * 0.85), (qMax, h0 * 0.5)], new Footprint(1000, 1500, 1300), new Footprint(830, 391, 1460), 150, "Grundfos", "тест");

    [Fact]
    public void Smallest_power_set_meeting_the_duty_point_is_chosen()
    {
        // Требуется 100 м³/ч и 45 м: малый не тянет напор, средний 1/1 — подача за пределом кривой; 2/1 подходит.
        var sets = new[] { Set("A", 1, 7.5, 40, 120), Set("B", 1, 15, 60, 80), Set("C", 2, 22, 60, 80), Set("D", 1, 55, 70, 160) }; // D подходит, но суммарная мощность основных больше (55 > 2 × 22)
        var r = PumpSetSelection.Choose(sets, 100, 45);
        r.Chosen!.Designation.ShouldBe("C");
        r.HeadAtDuty.ShouldBe(60 * 0.85 - ((50.0 - 40) / 40 * (60 * 0.85 - 30)), 1e-9); // по кривой одного насоса при Q/2 = 50
    }

    [Fact]
    public void No_suitable_set_is_an_issue()
    {
        var r = PumpSetSelection.Choose([Set("A", 1, 7.5, 40, 120)], 100, 80);
        r.Chosen.ShouldBeNull();
        r.Issues.ShouldContain(i => i.Code == "pump.noSet");
    }
}
