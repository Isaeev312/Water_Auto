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
