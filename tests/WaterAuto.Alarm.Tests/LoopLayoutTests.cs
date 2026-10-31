using WaterAuto.Alarm;
using WaterAuto.Core;

namespace WaterAuto.Alarm.Tests;

public class LoopLayoutTests
{
    private static readonly LoopController Kdl = new("С2000-КДЛ", new Current(0.08), new Current(0.16), new Current(0.0635), new Current(0.4), new Current(0.064), 127, "РЭ");
    private static readonly LoopModule Ar8 = new("С2000-АР8", 8, new Current(0.004), 15, "РЭ");

    [Fact]
    public void Modules_per_line_are_limited_by_the_tightest_passport_figure()
    {
        // АР8: 15 на линию; 127/8 = 15 адресов; 64/4 = 16 по току — предел 15.
        LoopLayout.Plan(8, Kdl, Ar8).ModulesPerLine.ShouldBe(15);
        // Модуль на 10 мА: по току 6, остальное шире.
        LoopLayout.Plan(8, Kdl, Ar8 with { LoopCurrent = new Current(0.01) }).ModulesPerLine.ShouldBe(6);
    }

    [Fact]
    public void Sixteen_modules_need_a_second_controller()
    {
        var plan = LoopLayout.Plan(16 * 8, Kdl, Ar8);

        plan.Modules.ShouldBe(16);
        plan.Controllers.ShouldBe(2);
        plan.ModulesByController.ShouldBe([15, 1]);
    }

    [Fact]
    public void Tiny_or_zero_module_current_does_not_overflow()
    {
        LoopLayout.Plan(8, Kdl, Ar8 with { LoopCurrent = new Current(1e-12) }).ModulesPerLine.ShouldBe(15);
        LoopLayout.Plan(8, Kdl, Ar8 with { LoopCurrent = Current.Zero }).ModulesPerLine.ShouldBe(15);
    }

    [Fact]
    public void Fewer_addresses_than_module_inputs_still_gives_one_module_per_line()
    {
        LoopLayout.Plan(8, Kdl with { MaxAddresses = 4 }, Ar8).ModulesPerLine.ShouldBe(1);
        Should.Throw<ArgumentOutOfRangeException>(() => LoopLayout.Plan(8, Kdl, Ar8 with { Inputs = 0 }));
    }

    [Fact]
    public void No_inputs_means_no_controllers()
    {
        LoopLayout.Plan(0, Kdl, Ar8).Controllers.ShouldBe(0);
    }
}
