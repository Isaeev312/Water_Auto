using ACadSharp.Entities;
using WaterAuto.Core;

namespace WaterAuto.Export.Tests;

public class PumpRoomDxfWriterTests
{
    [Fact]
    public void Plan_has_room_equipment_positions_service_zone_and_notes()
    {
        var room = new Polygon([new(0, 0), new(8000, 0), new(8000, 6000), new(0, 6000)]);
        var data = new PumpRoomDrawing(room, new PlanPoint(4000, 0),
        [
            new("Установка", 5000, 3000, 7000, 5000, 1),
            new("Шкаф", 0, 2000, 400, 2830, 1, (1, 0), 2000),
        ],
        ["Экспликация: 1 — установка."]);
        var doc = PumpRoomDxfWriter.Build(data, new TitleBlock("1-ПТ", "Объект", "Здание", "План насосной", "Р", "Орг.", []));
        doc.Entities.OfType<LwPolyline>().Count(p => p.Layer.Name == PumpRoomDxfWriter.EquipmentLayer).ShouldBe(2);
        doc.Entities.OfType<LwPolyline>().Count(p => p.Layer.Name == PlanDxfWriter.DimensionLayer).ShouldBe(1); // зона обслуживания
        doc.Entities.OfType<Arc>().Count(a => a.Layer.Name == PumpRoomDxfWriter.RoomLayer).ShouldBe(1); // дверь
        doc.Entities.OfType<MText>().ShouldContain(t => t.Value.Contains("Экспликация", StringComparison.Ordinal));
        doc.Entities.OfType<TextEntity>().ShouldContain(t => t.Value == "1");
    }
}
