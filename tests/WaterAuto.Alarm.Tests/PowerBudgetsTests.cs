using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// Питание приборов автоматики АУПТ в АПС от РИП с АКБ (ADR 0012, этап 1; прил. Б СП 6.13130.2025).
// Набор и числа — как в паспортах Болида (data/catalog/alarm_power.json); ожидаемые значения посчитаны вручную.
public class PowerBudgetsTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static readonly AlarmPowerSet Set = new(
        12,
        new LoopController("С2000-КДЛ", new Current(0.08), new Current(0.16), new Current(0.0635), new Current(0.4), new Current(0.064), 127, "РЭ КДЛ"),
        new LoopModule("С2000-АР8", 8, new Current(0.004), 15, "РЭ АР8"),
        new PowerSupply("РИП-12 исп.01", new Current(0.04), "РЭ РИП"),
        new BatteryModel("АБ 1217С", new Charge(17), 80, new Dictionary<double, Current> { [1] = new(10.41), [24] = new(0.727) }, "РЭ АБ"));

    [Fact]
    public void Eight_inputs_need_one_module_and_give_the_hand_computed_capacity()
    {
        var result = PowerBudgets.Build(8, Set, Norms);

        // Iд КДЛ = 80 + 80·4/63,5 мА (между двумя точками паспорта); Iп КДЛ = 400 мА (максимальный); Iибэ = 40 мА.
        // Kн.д = 17/(0,727·24); Kн.п = 17/(10,41·1); C = 1,25·(Kн.д·(Iд + Iибэ)·24 + Kн.п·(Iп + Iибэ)·1).
        var b = result.Budget;
        b.Lines.Single(l => l.Name == "С2000-АР8").Count.ShouldBe(1);
        b.Standby.Amperes.ShouldBe(0.08503937, 1e-8);
        b.Alarm.Amperes.ShouldBe(0.4, 1e-12);
        b.StandbyLoadFactor!.Value.ShouldBe(0.97432370, 1e-8);
        b.AlarmLoadFactor!.Value.ShouldBe(1.63304515, 1e-8);
        b.Required!.Value.AmpereHours.ShouldBe(4.5530395, 1e-6);
        // Б.3 для выбранной АКБ: t = 17 / (0,4·1,25) = 34 ч.
        b.Backup!.Value.TotalHours.ShouldBe(34, 1e-9);
        result.Issues.ShouldNotContain(i => i.Level != IssueLevel.Info);
    }

    [Fact]
    public void Module_current_is_inside_the_controller_current_and_not_added_again()
    {
        var result = PowerBudgets.Build(16, Set, Norms);

        var b = result.Budget;
        var modules = b.Lines.Single(l => l.Name == "С2000-АР8");
        modules.Count.ShouldBe(2);
        modules.Standby.ShouldBeNull();
        modules.Alarm.ShouldBeNull();
        // Только КДЛ по нагрузке ДПЛС 2·4 мА, без отдельного слагаемого модулей.
        b.Standby.Amperes.ShouldBe(0.08 + (0.08 * 0.008 / 0.0635), 1e-12);
    }

    [Fact]
    public void Passport_reading_choices_are_reported()
    {
        var codes = PowerBudgets.Build(8, Set, Norms).Issues.Select(i => i.Code).ToList();

        codes.ShouldContain("aps.power.controllerStandby");
        codes.ShouldContain("aps.power.controllerAlarm");
        codes.ShouldContain("aps.power.defaultSet");
    }

    [Fact]
    public void Missing_discharge_column_gives_no_capacity()
    {
        var set = Set with { Battery = Set.Battery with { Discharge = new Dictionary<double, Current> { [1] = new(10.41) } } };

        var result = PowerBudgets.Build(8, set, Norms);

        result.Budget.Required.ShouldBeNull();
        result.Issues.ShouldContain(i => i.Code == "aps.power.discharge" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Load_above_battery_or_loop_limits_is_a_warning()
    {
        // 16 АР8 — больше 15 на линии и 127 адресов. Kн ∝ C20, поэтому нехватку ёмкости задаёт таблица разряда:
        // 1,25·((Iд + Iибэ)/I24ч + (Iп + Iибэ)/I1ч) > 1 при токах разряда выше нагрузки (без dischargeLimit).
        var small = Set with { Battery = Set.Battery with { Discharge = new Dictionary<double, Current> { [1] = new(0.5), [24] = new(0.25) } } };

        var codes = PowerBudgets.Build(16 * 8, small, Norms).Issues.Where(i => i.Level == IssueLevel.Warning).Select(i => i.Code).ToList();

        codes.ShouldContain("aps.power.loopModules");
        codes.ShouldContain("aps.power.loopLoad");
        codes.ShouldContain("aps.power.capacity");
    }

    [Fact]
    public void Currents_above_discharge_table_are_a_warning()
    {
        var weak = Set with { Battery = Set.Battery with { Discharge = new Dictionary<double, Current> { [1] = new(0.3), [24] = new(0.05) } } };

        PowerBudgets.Build(8, weak, Norms).Issues.ShouldContain(i => i.Code == "aps.power.dischargeLimit" && i.Level == IssueLevel.Warning);
    }
}
