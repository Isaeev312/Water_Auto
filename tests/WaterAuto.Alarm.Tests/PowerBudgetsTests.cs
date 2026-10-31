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
        new Voltage(12),
        new LoopController("С2000-КДЛ", new Current(0.08), new Current(0.16), new Current(0.0635), new Current(0.4), new Current(0.064), 127, "РЭ КДЛ") { Certificate = "тест" },
        new LoopModule("С2000-АР8", 8, new Current(0.004), 15, "РЭ АР8") { Certificate = "тест" },
        new PowerSupply("РИП-12 исп.01", new Current(0.04), "РЭ РИП") { Certificate = "тест" },
        new BatteryModel("АБ 1217С", new Charge(17), 80, Table(10.41, 0.727), "РЭ АБ") { Certificate = "тест" });

    private static Dictionary<TimeSpan, Current> Table(double oneHour, double day) =>
        new() { [TimeSpan.FromHours(1)] = new(oneHour), [TimeSpan.FromHours(24)] = new(day) };

    [Fact]
    public void Set_with_zero_denominators_is_an_error_not_a_crash()
    {
        var bad = Set with { Controller = Set.Controller with { LoadOfLoaded = Current.Zero } };

        var result = PowerBudgets.Build(8, bad, Norms);

        result.Budget.Required.ShouldBeNull();
        result.Budget.Lines.ShouldBeEmpty();
        result.Issues.ShouldContain(i => i.Code == "aps.power.badSet" && i.Level == IssueLevel.Error);
        Should.NotThrow(() => PowerBudgets.Build(8, Set with { Module = Set.Module with { Inputs = 0 } }, Norms));
    }

    [Fact]
    public void Zero_inputs_still_count_one_controller_and_report_aging()
    {
        var b = PowerBudgets.Build(0, Set, Norms).Budget;

        b.Lines.Single(l => l.Name == "С2000-КДЛ").Count.ShouldBe(1);
        b.Lines.Single(l => l.Name == "С2000-АР8").Count.ShouldBe(0);
        b.AgingFactor!.Value.ShouldBe(1.25, 1e-12);
    }

    private static readonly AlarmPowerSet WithDetectors = Set with
    {
        Detectors = new Dictionary<string, LoopDevice>
        {
            ["smoke"] = new("ДИП-34А-03", new Current(0.0006), "РЭ ДИП"),
            ["heat"] = new("С2000-ИП-03", new Current(0.0006), "РЭ ИП"),
        },
    };

    [Fact]
    public void Detectors_add_line_load_and_may_require_a_second_controller()
    {
        // 8 входов — 1 АР8 (4 мА) + 120 дымовых по 0,6 мА = 76 мА > 64 мА → 2 контроллера; адресов 128 > 127 — тот же вывод.
        var result = PowerBudgets.Build(8, WithDetectors, Norms, new Dictionary<string, int> { ["smoke"] = 120 });

        var b = result.Budget;
        b.Lines.Single(l => l.Name == "С2000-КДЛ").Count.ShouldBe(2);
        b.Lines.Single(l => l.Name == "ДИП-34А-03").Count.ShouldBe(120);
        b.Alarm.Amperes.ShouldBe(0.8, 1e-12);
        // Дежурный: линия 1 — 4 мА + 60·0,6; линия 2 — 60·0,6 мА.
        var l1 = 0.004 + 0.036; var l2 = 0.036;
        b.Standby.Amperes.ShouldBe(0.16 + (0.08 * (l1 + l2) / 0.0635), 1e-12);
        result.Issues.ShouldContain(i => i.Code == "aps.power.controllers");
    }

    [Fact]
    public void Missing_certificates_are_one_warning_naming_the_devices()
    {
        // Приборы набора сертифицированы, извещатели — нет: предупреждение называет только применённый ДИП.
        var warn = PowerBudgets.Build(8, WithDetectors, Norms, new Dictionary<string, int> { ["smoke"] = 12 }).Issues.Single(i => i.Code == "aps.power.noCertificate");
        warn.Level.ShouldBe(IssueLevel.Warning);
        warn.Message.ShouldContain("ДИП-34А-03");
        warn.Message.ShouldNotContain("С2000-КДЛ");
        warn.Message.ShouldNotContain("С2000-ИП-03"); // не применён — не требуется

        var bare = Set with { Controller = Set.Controller with { Certificate = null } };
        PowerBudgets.Build(8, bare, Norms).Issues.Single(i => i.Code == "aps.power.noCertificate").Message.ShouldContain("С2000-КДЛ");
        PowerBudgets.Build(8, Set, Norms).Issues.ShouldNotContain(i => i.Code == "aps.power.noCertificate");
    }

    [Fact]
    public void Alarm_current_above_supply_rating_asks_for_more_supplies()
    {
        // 9 контроллеров по 0,4 А = 3,6 А > 3 А → 2 источника. 9 КДЛ получаем из 9·120 извещателей (адреса).
        var set = WithDetectors with { Supply = Set.Supply with { MaxOutput = new Current(3) } };

        var result = PowerBudgets.Build(8, set, Norms, new Dictionary<string, int> { ["smoke"] = 9 * 120 });

        result.Budget.Lines.Single(l => l.Name == "С2000-КДЛ").Count.ShouldBeGreaterThanOrEqualTo(9);
        var warn = result.Issues.Single(i => i.Code == "aps.power.supplyOverload");
        warn.Level.ShouldBe(IssueLevel.Warning);
        warn.Message.ShouldContain("не менее 2 источников");
        // Принято 2 РИП, нагрузка поровну: ёмкость АКБ — на один источник, у каждого своя батарея.
        result.Budget.Supplies.ShouldBe(2);
        result.Budget.AlarmPerSupply.Amperes.ShouldBe(result.Budget.Alarm.Amperes / 2, 1e-9);
        result.Budget.Lines.Single(l => l.Name == Set.Supply.Name).Count.ShouldBe(2);
        var single = PowerBudgets.Build(8, set with { Supply = Set.Supply with { MaxOutput = null } }, Norms, new Dictionary<string, int> { ["smoke"] = 9 * 120 });
        single.Budget.Supplies.ShouldBe(1);
        result.Budget.Required!.Value.AmpereHours.ShouldBeLessThan(single.Budget.Required!.Value.AmpereHours);
        PowerBudgets.Build(8, set, Norms).Issues.ShouldNotContain(i => i.Code == "aps.power.supplyOverload");
    }

    [Fact]
    public void Few_detectors_stay_on_one_controller_and_unknown_kind_is_an_error()
    {
        var ok = PowerBudgets.Build(8, WithDetectors, Norms, new Dictionary<string, int> { ["smoke"] = 12, ["heat"] = 0 });
        ok.Budget.Lines.Single(l => l.Name == "С2000-КДЛ").Count.ShouldBe(1);
        ok.Budget.Lines.ShouldNotContain(l => l.Name == "С2000-ИП-03");
        ok.Budget.Standby.Amperes.ShouldBe(0.08 + (0.08 * (0.004 + (12 * 0.0006)) / 0.0635), 1e-12);

        var bad = PowerBudgets.Build(8, Set, Norms, new Dictionary<string, int> { ["smoke"] = 12 });
        bad.Issues.ShouldContain(i => i.Code == "aps.power.noDetector" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Powered_devices_add_their_own_standby_and_alarm_currents_and_loop_load()
    {
        // 10 оповещателей: 1 мА дежурный / 60 мА «Пожар» каждый, 0,6 мА от линии, 1 адрес.
        var sounder = new PoweredDevice("С2000-ОПЗ", new Current(0.0006), 1, new Current(0.001), new Current(0.06), "РЭ", "серт.");

        var result = PowerBudgets.Build(8, Set, Norms, powered: [(sounder, 10)]);

        var b = result.Budget;
        b.Lines.Single(l => l.Name == "С2000-ОПЗ").Count.ShouldBe(10);
        // Контроллер: линия 4 + 6 мА; плюс 10 мА оповещателей в дежурном; «Пожар»: 400 + 600 мА.
        b.Standby.Amperes.ShouldBe(0.08 + (0.08 * 0.010 / 0.0635) + 0.010, 1e-12);
        b.Alarm.Amperes.ShouldBe(1.0, 1e-12);
        b.Lines.Single(l => l.Name == "С2000-КДЛ").Alarm!.Value.Amperes.ShouldBe(0.4, 1e-12);
        result.Issues.ShouldNotContain(i => i.Code == "aps.power.noCertificate");
    }

    [Fact]
    public void Second_controller_doubles_alarm_current_and_adds_its_idle_current()
    {
        // 16 АР8 — 15 на первой линии, 1 на второй. Iп = 2·400 мА; Iд = (80 + 80·60/63,5) + (80 + 80·4/63,5) мА.
        var result = PowerBudgets.Build(16 * 8, Set, Norms);

        var b = result.Budget;
        b.Lines.Single(l => l.Name == "С2000-КДЛ").Count.ShouldBe(2);
        b.Alarm.Amperes.ShouldBe(0.8, 1e-12);
        b.Standby.Amperes.ShouldBe(0.16 + (0.08 * 0.064 / 0.0635), 1e-12);
        result.Issues.ShouldContain(i => i.Code == "aps.power.controllers" && i.Level == IssueLevel.Info);
        result.Issues.ShouldNotContain(i => i.Code == "aps.power.loopModules" || i.Code == "aps.power.loopLoad");
    }

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
        var set = Set with { Battery = Set.Battery with { Discharge = new Dictionary<TimeSpan, Current> { [TimeSpan.FromHours(1)] = new(10.41) } } };

        var result = PowerBudgets.Build(8, set, Norms);

        result.Budget.Required.ShouldBeNull();
        result.Issues.ShouldContain(i => i.Code == "aps.power.discharge" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Load_above_battery_capacity_is_a_warning()
    {
        // Kн ∝ C20, поэтому нехватку ёмкости задаёт таблица разряда:
        // 1,25·((Iд + Iибэ)/I24ч + (Iп + Iибэ)/I1ч) > 1 при токах разряда выше нагрузки (без dischargeLimit).
        var small = Set with { Battery = Set.Battery with { Discharge = Table(0.5, 0.25) } };

        var codes = PowerBudgets.Build(8, small, Norms).Issues.Where(i => i.Level == IssueLevel.Warning).Select(i => i.Code).ToList();

        codes.ShouldContain("aps.power.capacity");
    }

    [Fact]
    public void Currents_above_discharge_table_are_a_warning()
    {
        var weak = Set with { Battery = Set.Battery with { Discharge = Table(0.3, 0.05) } };

        PowerBudgets.Build(8, weak, Norms).Issues.ShouldContain(i => i.Code == "aps.power.dischargeLimit" && i.Level == IssueLevel.Warning);
    }
}
