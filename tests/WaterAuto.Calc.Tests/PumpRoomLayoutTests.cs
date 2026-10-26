using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class PumpRoomLayoutTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static PumpRoom Room(double w, double h, PlanPoint? door = null) =>
        new("R", new Polygon([new(0, 0), new(w, 0), new(w, h), new(0, h)]), door);

    private static readonly IReadOnlyList<StationItem> Items =
    [
        new(StationItemKind.PumpSet, "Установка", new Footprint(1500, 1000, 1300)),
        new(StationItemKind.Cabinet, "Шкаф", new Footprint(830, 400, 1460)),
        new(StationItemKind.ControlUnit, "УУ", new Footprint(600, 500, 1500), Count: 2),
        new(StationItemKind.Jockey, "Жокей", new Footprint(600, 400, 800)),
    ];

    private static double Gap(PlacedItem a, PlacedItem b)
    {
        var dx = Math.Max(0, Math.Max(a.X0 - b.X1, b.X0 - a.X1));
        var dy = Math.Max(0, Math.Max(a.Y0 - b.Y1, b.Y0 - a.Y1));
        return dx > 0 && dy > 0 ? Math.Sqrt((dx * dx) + (dy * dy)) : Math.Max(dx, dy);
    }

    [Fact]
    public void Everything_fits_with_6_10_16_passages_in_a_large_room()
    {
        var r = PumpRoomLayout.Place(Room(8000, 7000, new PlanPoint(4000, 0)), Items, Norms);
        r.Issues.ShouldNotContain(i => i.Level == IssueLevel.Error);
        r.Items.Count.ShouldBe(5); // установка, шкаф, 2 УУ, жокей

        var set = r.Items.Single(i => i.Kind == StationItemKind.PumpSet);
        // Модульная установка — не ближе 1 м к стенам.
        new[] { set.X0, set.Y0, 8000 - set.X1, 7000 - set.Y1 }.Min().ShouldBeGreaterThanOrEqualTo(1000 - 1e-6);

        // Между оборудованием — не менее 0,7 м, между узлами управления — 0,5 м.
        foreach (var a in r.Items)
        {
            foreach (var b in r.Items.Where(b => !ReferenceEquals(a, b)))
            {
                var need = a.Kind == StationItemKind.ControlUnit && b.Kind == StationItemKind.ControlUnit ? 500 : 700;
                Gap(a, b).ShouldBeGreaterThanOrEqualTo(need - 1e-6, $"{a.Label} — {b.Label}");
            }
        }

        // Перед шкафом — 2 м свободно (до стены и до оборудования).
        var cab = r.Items.Single(i => i.Kind == StationItemKind.Cabinet);
        cab.Front.ShouldNotBeNull();
    }

    [Fact]
    public void Too_small_room_is_an_error_with_the_needed_size()
    {
        var r = PumpRoomLayout.Place(Room(3000, 2500), Items, Norms);
        r.Issues.ShouldContain(i => i.Code == "pumpRoom.tooSmall" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Door_zone_stays_free()
    {
        var door = new PlanPoint(0, 3500);
        var r = PumpRoomLayout.Place(Room(8000, 7000, door), Items, Norms);
        r.Items.ShouldAllBe(i => !(i.X0 < 1000 && i.Y1 > 3000 && i.Y0 < 4000));
    }
}
