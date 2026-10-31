using WaterAuto.Calc.Powder;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

// Цепи пуска модулей: модулей на выход — по току пуска модуля и току выхода блока; блоков — по числу выходов.
public class PowderStartTests
{
    private static readonly PowderStartUnit Kpb = new("С2000-КПБ", 6, 2.5, 6, 0.0015);

    [Fact]
    public void Outputs_follow_the_start_current_and_units_the_output_count()
    {
        // Ток пуска 0,7 А: на выход 2,5 / 0,7 → 3 модуля; помещения 24 и 5 модулей → 8 + 2 = 10 выходов → 2 блока.
        var plan = PowderStart.Plan([("R1", 24), ("R2", 5)], 0.7, 0.15, Kpb);

        plan.ModulesPerOutput.ShouldBe(3);
        plan.OutputsByRoom["R1"].ShouldBe(8);
        plan.OutputsByRoom["R2"].ShouldBe(2);
        plan.Units.ShouldBe(2);
        plan.Issues.ShouldNotContain(i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Monitoring_current_above_the_module_safe_current_and_missing_start_current_are_errors()
    {
        PowderStart.Plan([("R1", 4)], 0.7, 0.001, Kpb).Issues.ShouldContain(i => i.Code == "powder.start.monitor" && i.Level == IssueLevel.Error);
        PowderStart.Plan([("R1", 4)], null, 0.15, Kpb).Issues.ShouldContain(i => i.Code == "powder.start.noCurrent");
    }

    [Fact]
    public void Room_start_current_above_the_unit_total_is_flagged()
    {
        // 24 модуля × 0,7 А = 16,8 А одновременно на помещение > 6 А блока: пуск выходов — последовательно с интервалом (≤ 3 с, п. 10.2.4).
        PowderStart.Plan([("R1", 24)], 0.7, 0.15, Kpb).Issues.ShouldContain(i => i.Code == "powder.start.total" && i.Level == IssueLevel.Warning);
    }
}
