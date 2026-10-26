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
        doc.Entities.OfType<Arc>().Count(a => a.Layer.Name == PumpSchemeDxfWriter.EquipmentLayer).ShouldBe(2 + 1); // 2 головки + обозначение в таблице
        doc.Entities.ShouldContain(e => e.Layer.Name == GostSheet.Layer);
    }
}
