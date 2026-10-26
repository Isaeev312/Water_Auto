using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

public class PumpRoomFeaturesTests
{
    private static readonly Polygon Room = new([new(0, 0), new(8000, 0), new(8000, 6000), new(0, 6000)]);

    [Fact]
    public void Pit_goes_to_a_free_corner_away_from_equipment_and_door()
    {
        // Установка — в левом нижнем углу, дверь — у правого верхнего.
        var items = new[] { new PlacedItem(StationItemKind.PumpSet, "Установка", 1000, 1000, 2500, 2500) };
        var f = PumpRoomFeatures.Build(Room, items, new PlanPoint(7500, 6000), pitMm: 800, foundationMarginMm: 100);
        f.Pit.ShouldNotBeNull();
        var p = f.Pit!.Value;
        (p.X1 - p.X0).ShouldBe(800, 1e-6);
        // Не левый нижний (установка) и не правый верхний (дверь).
        (p.X0 < 100 && p.Y0 < 100).ShouldBeFalse();
        (p.X1 > 7900 && p.Y1 > 5900).ShouldBeFalse();
        f.Foundation.ShouldBe((900, 900, 2600, 2600));
    }
}
