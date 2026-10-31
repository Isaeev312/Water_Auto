using WaterAuto.Calc.Powder;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

// Количество модулей порошкового пожаротушения по прил. И СП 485: N = S/Sн·k1·k2·k3·k4 (И.3), N = V/Vн·k1·k2·k3·k4 (И.1).
public class PowderModulesTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static PowderValues V(double aA, double aB, double vA, double vB) => new(aA, aB, vA, vB);

    // Модуль условный: 2,5–4 м — постоянно (A 30 / B 20 м², 50 / 40 м³); 4–6 м — линейно до A 24 / B 16, 40 / 30 м³.
    // k2 по формуле паспорта 1 + 1,33·Sз/Sу (порог 0,15); k1 = 1,0.
    private static readonly PowderModuleModel Module = new("m", "МПП-тест", PowderKind.AB, false,
        [new(2.5, 4, V(30, 20, 50, 40), V(30, 20, 50, 40)), new(4, 6, V(30, 20, 50, 40), V(24, 16, 40, 30))],
        K1: 1.0, K2Factor: 1.33, K2Limit: 0.15, K4Area: null, K4Volume: null, "паспорт");

    private static PowderRoom Room(PowderMethod method = PowderMethod.Area, double area = 100, double volume = 300, double height = 3, char cls = 'B', string? row = "sp485.tI_1.petrol", double f = 0, double? shaded = 0) =>
        new("R1", "Склад ГСМ", area, volume, height, method, cls, row, null, f, null, ShadedAreaM2: shaded);

    [Fact]
    public void Area_method_follows_I3_with_k4_1_2_and_rounds_up()
    {
        // 100 / 20 · 1,0 · 1,0 (Sз = 0) · 1,0 (бензин, порошок АВ) · 1,2 = 6,0 → 6 модулей.
        var r = PowderModules.Calculate([Room()], Module, Norms);

        var room = r.Rooms.Single();
        room.PerModule.ShouldBe(20);
        room.K2.ShouldBe(1.0);
        room.K4.ShouldBe(1.2);
        room.Modules.ShouldBe(6);
        r.Reserve.ShouldBe(6);
    }

    [Fact]
    public void K2_follows_the_passport_formula_with_the_designer_shaded_area()
    {
        // Sз = 10 м² из 100: k2 = 1 + 1,33·0,1 = 1,133; 100/20·1,133·1,2 = 6,8 → 7.
        var room = PowderModules.Calculate([Room(shaded: 10)], Module, Norms).Rooms.Single();
        room.K2.ShouldBe(1.133, 1e-9);
        room.Modules.ShouldBe(7);

        // Sз/Sу больше порога паспорта — предупреждение с рекомендацией изготовителя, k2 не меняется сам.
        var heavy = PowderModules.Calculate([Room(shaded: 20)], Module, Norms);
        heavy.Issues.ShouldContain(i => i.Code == "powder.k2.shading" && i.Level == IssueLevel.Warning);
        heavy.Rooms.Single().K2.ShouldBe(1.266, 1e-9);

        // Затенённая площадь не задана — ошибка (ноль — только решение проектировщика).
        PowderModules.Calculate([Room(shaded: null)], Module, Norms).Issues.ShouldContain(i => i.Code == "powder.k2.noShading" && i.Level == IssueLevel.Error);

        // В паспорте нет k2 — ошибка без подстановки.
        var noK2 = PowderModules.Calculate([Room()], Module with { K2Factor = null }, Norms);
        noK2.Issues.ShouldContain(i => i.Code == "powder.k2.missing" && i.Level == IssueLevel.Error);
        noK2.Rooms.ShouldBeEmpty();
    }

    [Fact]
    public void Local_area_adds_10_percent_and_uses_k4_1_3()
    {
        // (100 · 1,1) / 20 · 1,0 · 1,0 · 1,0 · 1,3 = 7,15 → 8.
        var room = PowderModules.Calculate([Room(PowderMethod.LocalArea)], Module, Norms).Rooms.Single();

        room.Protected.ShouldBe(110, 1e-9);
        room.K4.ShouldBe(1.3);
        room.Modules.ShouldBe(8);
    }

    [Fact]
    public void Volume_uses_the_class_volume_and_k4_from_open_fraction_even_if_the_passport_gives_k4_for_area()
    {
        // Класс B по объёму: Vн = 40 м³; 300 / 40 · 1,0 · 1,0 · 1,0 · (1 + 10·0,01) = 8,25 → 9. k4 паспорта «по площади» на объём не действует.
        var r = PowderModules.Calculate([Room(PowderMethod.Volume, f: 0.01)], Module with { K4Area = 1.0 }, Norms);
        r.Rooms.Single().PerModule.ShouldBe(40);
        r.Rooms.Single().K4.ShouldBe(1.1, 1e-9);
        r.Rooms.Single().Modules.ShouldBe(9);

        // По площади k4 паспорта (1,0) — вместо 1,2 свода.
        PowderModules.Calculate([Room()], Module with { K4Area = 1.0 }, Norms).Rooms.Single().K4.ShouldBe(1.0);

        // Объём свыше 400 м³ — по объёму только по СТО (10.2.9).
        var big = PowderModules.Calculate([Room(PowderMethod.Volume, volume: 500)], Module, Norms);
        big.Issues.ShouldContain(i => i.Code == "powder.volume.tooLarge" && i.Level == IssueLevel.Error);
        big.Rooms.ShouldBeEmpty();
    }

    [Fact]
    public void Coverage_is_interpolated_inside_a_segment_and_conservative_between_points()
    {
        // 5 м — середина отрезка 4–6 м: B = (20 + 16) / 2 = 18 м².
        PowderModules.Calculate([Room(height: 5)], Module, Norms).Rooms.Single().PerModule.ShouldBe(18, 1e-9);

        // Таблица точками, площадь растёт с высотой (как у Гарант-12): 6 м — 40, 8 м — 49; на 7 м — меньшее, 40, а не 49.
        var growing = Module with { Coverage = [new(6, 6, V(40, 25, 104, 50), V(40, 25, 104, 50)), new(8, 8, V(49, 35, 98, 70), V(49, 35, 98, 70))] };
        PowderModules.Calculate([Room(height: 7, cls: 'A')], growing, Norms).Rooms.Single().PerModule.ShouldBe(40);

        // Выше последнего и ниже первого отрезка — нет данных паспорта: ошибка.
        PowderModules.Calculate([Room(height: 7)], Module, Norms).Issues.ShouldContain(i => i.Code == "powder.height" && i.Level == IssueLevel.Error);
        PowderModules.Calculate([Room(height: 2)], Module, Norms).Issues.ShouldContain(i => i.Code == "powder.height");
    }

    [Fact]
    public void K1_and_k3_come_from_the_passport_or_the_table_and_wood_with_manual_start_uses_the_bracket_value()
    {
        // k1 нет ни в паспорте, ни в задании, высота ниже предельной — ошибка; на предельной — 1,2.
        PowderModules.Calculate([Room()], Module with { K1 = null }, Norms).Issues.ShouldContain(i => i.Code == "powder.k1.missing");
        PowderModules.Calculate([Room(height: 6)], Module with { K1 = null }, Norms).Rooms.Single().K1.ShouldBe(1.2);

        PowderModules.Calculate([Room(row: null)], Module, Norms).Issues.ShouldContain(i => i.Code == "powder.k3.missing");
        PowderModules.Calculate([Room(cls: 'A', row: "sp485.tI_1.wood")], Module, Norms).Rooms.Single().K3.ShouldBe(1.0);
        PowderModules.Calculate([Room(cls: 'A', row: "sp485.tI_1.wood") with { ManualOnly = true }], Module, Norms).Rooms.Single().K3.ShouldBe(2.0);
    }
}
