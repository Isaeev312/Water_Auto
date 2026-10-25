using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class FireParametersTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static FireParameters Ok(string group, double? room = null, double? storage = null)
    {
        var r = FireParameters.Resolve(Norms, group, room is { } h ? new Length(h) : null, storage is { } s ? new Length(s) : null);
        r.Issues.Where(i => i.Level == IssueLevel.Error).ShouldBeEmpty(string.Join("; ", r.Issues.Select(i => i.Message)));
        return r.Parameters!;
    }

    private static Issue Error(string group, double? room = null, double? storage = null)
    {
        var r = FireParameters.Resolve(Norms, group, room is { } h ? new Length(h) : null, storage is { } s ? new Length(s) : null);
        r.Parameters.ShouldBeNull();
        return r.Issues.ShouldHaveSingleItem();
    }

    [Fact]
    public void Low_rooms_take_table_6_1()
    {
        var p = Ok("2", room: 3.5);
        (p.Intensity, p.Flow.LitersPerSecond, p.MinArea, p.Duration, p.MaxSpacing.Meters).ShouldBe((0.12, 30.0, 120.0, 60.0, 3.5));
        p.Source.ShouldBe("табл. 6.1");
    }

    [Theory]
    [InlineData(10.0, 0.13, 35, 132)] // «от 10» — строже табл. 6.1
    [InlineData(11.0, 0.13, 35, 132)]
    [InlineData(12.0, 0.13, 35, 132)] // «до 12 включ.»
    [InlineData(12.5, 0.14, 40, 144)]
    [InlineData(20.0, 0.18, 65, 180)]
    public void High_rooms_take_table_6_3(double room, double i, double q, double s)
    {
        var p = Ok("2", room: room);
        (p.Intensity, p.Flow.LitersPerSecond, p.MinArea).ShouldBe((i, q, s));
        (p.Duration, p.MaxSpacing.Meters).ShouldBe((60.0, 3.5)); // из табл. 6.1
        p.Source.ShouldBe("табл. 6.3");
    }

    [Fact]
    public void Rooms_above_20_m_are_outside_sprinkler_tables()
    {
        Error("2", room: 21).Code.ShouldBe("params.roomTooHigh");
    }

    [Theory]
    [InlineData("5", 0.5, 0.08, 15)]
    [InlineData("5", 1.0, 0.08, 15)] // «до 1 включ.»
    [InlineData("5", 3.5, 0.32, 60)]
    [InlineData("6", 1.0, 0.16, 30)]
    [InlineData("6", 5.5, 0.50, 90)]
    public void Warehouses_take_table_6_2_by_storage_height(string group, double storage, double i, double q)
    {
        var p = Ok(group, room: 8, storage: storage);
        (p.Intensity, p.Flow.LitersPerSecond).ShouldBe((i, q));
        (p.MinArea, p.Duration, p.MaxSpacing.Meters).ShouldBe((90.0, 60.0, 3.0)); // табл. 6.1
        p.Source.ShouldBe("табл. 6.2");
    }

    [Fact]
    public void High_warehouse_rooms_scale_by_note_3()
    {
        // H = 14 м: [1 + 0,05·(14 − 10)] = 1,2 → i = 0,32·1,2 = 0,384; Q = 60·1,2 = 72.
        var p = Ok("5", room: 14, storage: 3.5);
        p.Intensity.ShouldBe(0.384, 1e-12);
        p.Flow.LitersPerSecond.ShouldBe(72, 1e-12);
        p.HeightFactor.ShouldBe(1.2, 1e-12);
        p.Source.ShouldBe("табл. 6.2, прим. 3");
    }

    [Fact]
    public void Warehouse_needs_storage_height_and_stays_within_the_table()
    {
        Error("5", room: 8).Code.ShouldBe("params.noStorageHeight");
        Error("5", room: 8, storage: 6).Code.ShouldBe("params.storageTooHigh");
        Error("6", room: 31, storage: 3).Code.ShouldBe("params.roomTooHigh");
    }

    [Fact]
    public void Group_7_is_not_extinguished_with_water()
    {
        Error("7", room: 8, storage: 2).Code.ShouldBe("params.noWater");
    }
}
