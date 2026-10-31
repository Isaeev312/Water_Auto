using ACadSharp.Entities;
using WaterAuto.Core;

namespace WaterAuto.Export.Tests;

public class PumpSchemeDxfWriterTests
{
    private static readonly TitleBlock Stamp = new("123-ПТ", "Автостоянка", "Корпус 1", "Принципиальная схема насосной станции", "Р", "Организация", [new("Разраб.", "Иванов")]);

    private static readonly PumpSchemeData Data = new(2, 150, 100, 150, 2, 80, "Н1, Н2 — Q = 167 м³/ч, H = 49 м", "НЖ — жокей-насос", "Из наружной сети", ["Примечание."]);

    [Fact]
    public void Scheme_has_pumps_truck_inlets_frame_and_DN_labels()
    {
        var doc = PumpSchemeDxfWriter.Build(Data, Stamp);
        var texts = doc.Entities.OfType<TextEntity>().Select(t => t.Value).Concat(doc.Entities.OfType<MText>().Select(t => t.Value)).ToList();
        texts.ShouldContain("Н1");
        texts.ShouldContain("Н2");
        texts.ShouldContain("НЖ");
        texts.ShouldContain("DN150");
        texts.ShouldContain("DN100");
        texts.ShouldContain(t => t.Contains("ГМ-80", StringComparison.Ordinal));
        texts.ShouldContain("Условные обозначения");
        doc.Entities.OfType<Arc>().Count(a => a.Layer.Name == PumpSchemeDxfWriter.EquipmentLayer).ShouldBe(2 + 1 + 2); // 2 головки, обозначение в таблице, 2 заглушки
        doc.Entities.ShouldContain(e => e.Layer.Name == GostSheet.Layer);
    }

    [Fact]
    public void Positions_of_the_station_specification_are_marked_on_the_scheme()
    {
        var doc = PumpSchemeDxfWriter.Build(Data with { Positions = new Dictionary<string, int> { ["pump"] = 1, ["teeS"] = 15, ["head"] = 12 } }, Stamp);
        var texts = doc.Entities.OfType<TextEntity>().Select(t => t.Value).ToList();
        texts.ShouldContain("1");
        texts.ShouldContain("15");
        texts.ShouldContain("12");
        texts.ShouldNotContain("16");
    }

    [Fact]
    public void Scheme_scales_to_four_pumps_and_three_sections_within_the_sheet()
    {
        var doc = PumpSchemeDxfWriter.Build(Data with { Pumps = 4, Outlets = ["точка ввода 1", "точка ввода 2", "точка ввода 3"] }, Stamp);
        var texts = doc.Entities.OfType<TextEntity>().Select(t => t.Value).ToList();
        texts.ShouldContain("Н4");
        texts.Count(t => t.StartsWith("Секция", StringComparison.Ordinal)).ShouldBe(3);
        // Всё — внутри рамки A3 (правый край рамки — 415 мм).
        doc.Entities.OfType<Line>().Where(l => l.Layer.Name != GostSheet.Layer).ShouldAllBe(l => l.StartPoint.X <= 415 && l.EndPoint.X <= 415);
    }

    [Fact]
    public void Signal_marks_of_the_aps_list_are_written_at_their_devices()
    {
        // Обозначения — как в перечне сигналов АПС: затворы секции — до и после узла управления подряд.
        var marks = new Dictionary<string, IReadOnlyList<string>>
        {
            ["inletValve"] = ["ЗД1", "ЗД2"],
            ["valveS"] = ["ЗД3", "ЗД4"],
            ["valveD"] = ["ЗД5", "ЗД6"],
            ["cuValve"] = ["ЗД7", "ЗД8", "ЗД9", "ЗД10"],
            ["cu"] = ["УУ1", "УУ2"],
            ["cabinet"] = ["ШУН"],
        };
        var doc = PumpSchemeDxfWriter.Build(Data with { Inlets = 2, Outlets = ["т. 1", "т. 2"], Marks = marks }, Stamp);
        var texts = doc.Entities.OfType<TextEntity>().ToList();

        foreach (var mark in marks.Values.SelectMany(m => m))
        {
            texts.Count(t => t.Value == mark).ShouldBe(1, mark);
        }

        texts.ShouldNotContain(t => t.Value == "УУ" || t.Value == "ШУ");
        // Затвор до узла второй секции (ЗД9) — ниже затвора после узла (ЗД10) по ходу воды вверх по листу.
        texts.Single(t => t.Value == "ЗД9").InsertPoint.Y.ShouldBeLessThan(texts.Single(t => t.Value == "ЗД10").InsertPoint.Y);
        texts.Single(t => t.Value == "ЗД9").InsertPoint.X.ShouldBeGreaterThan(texts.Single(t => t.Value == "ЗД7").InsertPoint.X);
    }

    [Fact]
    public void Marks_follow_four_pumps_and_a_short_list_leaves_the_rest_unmarked()
    {
        var marks = new Dictionary<string, IReadOnlyList<string>> { ["valveS"] = ["ЗД1", "ЗД2", "ЗД3"] };
        var doc = PumpSchemeDxfWriter.Build(Data with { Pumps = 4, Marks = marks }, Stamp);
        var texts = doc.Entities.OfType<TextEntity>().Select(t => t.Value).ToList();

        texts.Count(t => t.StartsWith("ЗД", StringComparison.Ordinal)).ShouldBe(3);
        texts.ShouldContain("Н4");
    }

    [Fact]
    public void Pump_line_valves_without_sensors_are_drawn_plain()
    {
        var with = PumpSchemeDxfWriter.Build(Data, Stamp).Entities.Count(e => e.Layer.Name == PumpSchemeDxfWriter.EquipmentLayer);
        var without = PumpSchemeDxfWriter.Build(Data with { PumpLineSensors = false }, Stamp).Entities.Count(e => e.Layer.Name == PumpSchemeDxfWriter.EquipmentLayer);

        // Два агрегата × два затвора — четыре датчика меньше.
        without.ShouldBeLessThan(with);
    }

    [Fact]
    public void Each_inlet_has_its_own_branch_and_valve_on_the_scheme()
    {
        // Inlets=2 (п. 6.10.27): на всасывающем коллекторе две ветви ввода, у каждой — своя задвижка с номером позиции.
        var positions = new Dictionary<string, int> { ["inletValve"] = 5 };
        var two = PumpSchemeDxfWriter.Build(Data with { Inlets = 2, Positions = positions }, Stamp);
        var twoTexts = two.Entities.OfType<TextEntity>().Select(t => t.Value).ToList();
        twoTexts.Count(t => t == "5").ShouldBe(2); // по выноске на каждый ввод
        twoTexts.ShouldContain("Ввод 2");
        twoTexts.ShouldContain(t => t.Contains("Из наружной сети", StringComparison.Ordinal)); // подпись источника остаётся

        // По умолчанию (Inlets=1) — один ввод и одна задвижка.
        var one = PumpSchemeDxfWriter.Build(Data with { Positions = positions }, Stamp);
        var oneTexts = one.Entities.OfType<TextEntity>().Select(t => t.Value).ToList();
        oneTexts.Count(t => t == "5").ShouldBe(1);
        oneTexts.ShouldNotContain("Ввод 2");
    }
}
