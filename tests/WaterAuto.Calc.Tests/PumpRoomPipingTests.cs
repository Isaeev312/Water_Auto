using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

public class PumpRoomPipingTests
{
    private static readonly Polygon Room = new([new(0, 0), new(8000, 0), new(8000, 6000), new(0, 6000)]);
    private static readonly PipingRules Rules = new(WallOffsetMm: 250, BracketHeightMm: 2400, LevelGapMm: 300);

    [Fact]
    public void Run_goes_along_the_walls_on_brackets_and_drops_to_the_connection()
    {
        // Ввод в левой стене на высоте кронштейнов; патрубок установки — (5000, 4000) на высоте 200 мм; труба Ø108.
        var r = PumpRoomPiping.Route(Room, [new PipeRunRequest("Всасывающий", 100, 108, new(0, 3000), null, new(5000, 4000), 200, Level: 0)], Rules);
        r.Issues.ShouldBeEmpty();
        var run = r.Runs.ShouldHaveSingleItem();
        var d = 250 + 54.0; // от стены до оси
        run.Path.ShouldBe([new(0, 3000), new(d, 3000), new(d, 6000 - d), new(5000, 6000 - d), new(5000, 4000)]);
        run.HorizontalMm.ShouldBe(d + (3000 - d) + (5000 - d) + (2000 - d), 1e-6);
        run.VerticalMm.ShouldBe(2400 - 200, 1e-6);
        run.Elbows.ShouldBe(3 + 2); // три поворота в плане и опуск (два отвода)
    }

    [Fact]
    public void Second_level_is_offset_from_the_first_so_pipes_do_not_overlap_in_plan()
    {
        var r = PumpRoomPiping.Route(Room,
        [
            new PipeRunRequest("Всасывающий", 100, 108, new(0, 3000), null, new(5000, 4000), 200, Level: 0),
            new PipeRunRequest("Напорный", 100, 108, new(5000, 4000), 1300, new(8000, 2000), null, Level: 1),
        ], Rules);
        var b = r.Runs[1];
        b.Path.ShouldAllBe(p => p.XMm <= 8000 + 1e-6);
        // Напорный идёт по стене дальше от неё на диаметр и зазор.
        var dWall = 8000 - b.Path[^2].XMm;
        dWall.ShouldBe(250 + 54 + 300 + 108, 1e-6);
        b.Path[^1].ShouldBe(new PlanPoint(8000, 2000));
    }

    [Fact]
    public void Equipment_to_equipment_goes_straight_over_the_aisle()
    {
        var r = PumpRoomPiping.Route(Room, [new PipeRunRequest("К УУ", 150, 159, new(2000, 2000), 1300, new(3000, 4000), 1000, Level: 1)], Rules);
        var run = r.Runs.ShouldHaveSingleItem();
        run.Path.ShouldBe([new(2000, 2000), new(3000, 2000), new(3000, 4000)]);
        run.HorizontalMm.ShouldBe(3000, 1e-6);
        run.VerticalMm.ShouldBe((2400 - 1300) + (2400 - 1000), 1e-6);
        run.Elbows.ShouldBe(1 + 2 + 2);
    }

    [Fact]
    public void Wall_point_far_from_walls_is_an_issue()
    {
        var r = PumpRoomPiping.Route(Room, [new PipeRunRequest("Ввод", 100, 108, new(4000, 3000), null, new(5000, 4000), 200, Level: 0)], Rules);
        r.Issues.ShouldContain(i => i.Code == "pumpRoom.pointNotOnWall");
    }
}
