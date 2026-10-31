using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// Проверки ЗКСПС и ёмкости ППКП (СП 484, 6.3.4, 6.1.5, 5.20) по данным норм; числа — из data/norms/sp484.
public class ZoneChecksTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static Polygon Rect(double wMm, double hMm) => new([new(0, 0), new(wMm, 0), new(wMm, hMm), new(0, hMm)]);

    private static DetectorZone Zone(string id, Polygon outline) => new(id, id, outline, new Length(3), DetectorKind.Smoke, DetectionAlgorithm.A, true);

    private static DetectorLayout Layout(params (string Room, int Count)[] counts) =>
        new([.. counts.SelectMany(c => Enumerable.Range(1, c.Count).Select(n => new PlacedDetector($"{c.Room}.D{n}", c.Room, new PlanPoint(0, 0), DetectorKind.Smoke)))],
            counts.ToDictionary(c => c.Room, c => c.Count), []);

    [Fact]
    public void Room_larger_than_a_zone_needs_splitting()
    {
        // 60 × 40 м = 2400 м² > 2000 м² → не меньше двух ЗКСПС.
        var issues = ZoneChecks.Check([Zone("R1", Rect(60000, 40000))], Layout(("R1", 30)), Norms);

        var split = issues.ShouldHaveSingleItem();
        split.Code.ShouldBe("aps.zone.split");
        split.Level.ShouldBe(IssueLevel.Warning);
        split.Message.ShouldContain("2 ЗКСПС");
        split.Clause!.ShouldContain("6.3.4");
    }

    [Fact]
    public void Small_room_passes_silently()
    {
        ZoneChecks.Check([Zone("R1", Rect(20000, 10000))], Layout(("R1", 6)), Norms).ShouldBeEmpty();
    }

    [Fact]
    public void Panel_capacity_counts_all_detectors_and_area()
    {
        // 5 помещений по 40 × 40 м = 8000 м² (по 1600 м² — одна ЗКСПС каждое), 120 ИП в каждом → 600 > 512 и 8000 ≤ 12 000.
        var zones = Enumerable.Range(1, 5).Select(i => Zone($"R{i}", Rect(40000, 40000))).ToList();
        var layout = Layout([.. zones.Select(z => (z.RoomId, 120))]);

        var issues = ZoneChecks.Check(zones, layout, Norms);

        var panel = issues.Single(i => i.Code == "aps.panel.detectors");
        panel.Level.ShouldBe(IssueLevel.Warning);
        panel.Message.ShouldContain("600");
        panel.Clause!.ShouldContain("6.1.5");
        issues.ShouldNotContain(i => i.Code == "aps.panel.area");
    }

    [Fact]
    public void Reserve_of_twenty_percent_is_reported_when_capacity_is_nearly_full()
    {
        // 450 ИП: до 512 запас 13,8 % < 20 % (5.20) — Info, не ошибка.
        var issues = ZoneChecks.Check([Zone("R1", Rect(40000, 40000))], Layout(("R1", 450)), Norms);

        var reserve = issues.Single(i => i.Code == "aps.panel.reserve");
        reserve.Level.ShouldBe(IssueLevel.Info);
        reserve.Clause!.ShouldContain("5.20");
    }
}
