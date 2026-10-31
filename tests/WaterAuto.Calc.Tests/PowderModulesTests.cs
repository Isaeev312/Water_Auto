using WaterAuto.Calc.Powder;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

// Количество модулей порошкового пожаротушения по прил. И СП 485: N = S/Sн·k1·k2·k3·k4 (И.3), N = V/Vн·k1·k2·k3·k4 (И.1).
public class PowderModulesTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    // Модуль условный: на 4 м — 20 м² по классу B, 30 м² по А; на 6 м (максимум) — 16 м² по B; объём 50 м³; k2 = 1,2 из паспорта.
    private static readonly PowderModuleModel Module = new("m", "МПП-тест", PowderKind.AB, false, 2.5, 6,
        [new(4, 30, 20, 50), new(6, 24, 16, 40)], K1: null, K2: 1.2, K4: null, "паспорт");

    private static PowderRoom Room(PowderMethod method = PowderMethod.Area, double area = 100, double volume = 300, double height = 4, char cls = 'B', string? row = "sp485.tI_1.petrol", double f = 0, double? k1 = 1.0) =>
        new("R1", "Склад ГСМ", area, volume, height, method, cls, row, null, f, k1);

    [Fact]
    public void Area_method_follows_I3_with_k4_1_2_and_rounds_up()
    {
        // 100 / 20 · 1,0 · 1,2 · 1,0 (бензин, порошок АВ) · 1,2 = 7,2 → 8 модулей.
        var r = PowderModules.Calculate([Room()], Module, Norms);

        var room = r.Rooms.Single();
        room.PerModule.ShouldBe(20);
        room.K2.ShouldBe(1.2);
        room.K3.ShouldBe(1.0);
        room.K4.ShouldBe(1.2);
        room.Modules.ShouldBe(8);
        r.Reserve.ShouldBe(8);
        r.Issues.ShouldNotContain(i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Local_area_adds_10_percent_and_uses_k4_1_3()
    {
        // (100 · 1,1) / 20 · 1,0 · 1,2 · 1,0 · 1,3 = 8,58 → 9.
        var room = PowderModules.Calculate([Room(PowderMethod.LocalArea)], Module, Norms).Rooms.Single();

        room.Protected.ShouldBe(110, 1e-9);
        room.K4.ShouldBe(1.3);
        room.Modules.ShouldBe(9);
    }

    [Fact]
    public void Volume_method_uses_k4_from_open_fraction_and_checks_limits()
    {
        // 300 / 50 · 1,0 · 1,2 · 1,0 · (1 + 10·0,01) = 7,92 → 8.
        var r = PowderModules.Calculate([Room(PowderMethod.Volume, f: 0.01)], Module, Norms);
        r.Rooms.Single().K4.ShouldBe(1.1, 1e-9);
        r.Rooms.Single().Modules.ShouldBe(8);

        // Объём свыше 400 м³ — по объёму только по СТО (10.2.9): ошибка, модули не считаются.
        var big = PowderModules.Calculate([Room(PowderMethod.Volume, volume: 500)], Module, Norms);
        big.Issues.ShouldContain(i => i.Code == "powder.volume.tooLarge" && i.Level == IssueLevel.Error);
        big.Rooms.ShouldBeEmpty();
    }

    [Fact]
    public void Missing_k2_k1_or_substance_is_an_error_without_a_default()
    {
        // k2 по своду не задан — только паспорт.
        var noK2 = PowderModules.Calculate([Room()], Module with { K2 = null }, Norms);
        noK2.Issues.ShouldContain(i => i.Code == "powder.k2.missing" && i.Level == IssueLevel.Error);
        noK2.Rooms.ShouldBeEmpty();

        // k1 не задан и высота ниже максимальной — правила выбора внутри 1…1,2 нет (А-17).
        PowderModules.Calculate([Room(k1: null)], Module, Norms).Issues.ShouldContain(i => i.Code == "powder.k1.missing");
        // На максимальной высоте — 1,2 по И.3.1.1.
        PowderModules.Calculate([Room(height: 6, k1: null)], Module, Norms).Rooms.Single().K1.ShouldBe(1.2);

        // Вещество не из табл. И.1 и k3 не задан — ошибка.
        PowderModules.Calculate([Room(row: null)], Module, Norms).Issues.ShouldContain(i => i.Code == "powder.k3.missing");
    }

    [Fact]
    public void Height_outside_the_passport_range_is_an_error_and_coverage_is_taken_conservatively()
    {
        PowderModules.Calculate([Room(height: 7)], Module, Norms).Issues.ShouldContain(i => i.Code == "powder.height" && i.Level == IssueLevel.Error);

        // 5 м — между строками 4 и 6 м: принимается меньшая площадь из строк не ниже высоты установки (16 м²).
        PowderModules.Calculate([Room(height: 5)], Module, Norms).Rooms.Single().PerModule.ShouldBe(16);
    }

    [Fact]
    public void Wood_with_manual_start_uses_the_bracket_value_of_table_I1()
    {
        // Древесина: 1,0, для ручного пуска и импульсных модулей — 2,0 (в скобках).
        var auto = PowderModules.Calculate([Room(cls: 'A', row: "sp485.tI_1.wood")], Module, Norms).Rooms.Single();
        auto.K3.ShouldBe(1.0);
        auto.PerModule.ShouldBe(30);
        var manual = PowderModules.Calculate([Room(cls: 'A', row: "sp485.tI_1.wood") with { ManualOnly = true }], Module, Norms).Rooms.Single();
        manual.K3.ShouldBe(2.0);
    }
}
