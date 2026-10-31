using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// ИПР (6.6.27), деление на ЗКСПС (6.3.4) и изоляторы КЗ (6.3.5) после раскладки автоматических ИП.
public class AlarmZoningTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static Polygon Rect(double wMm, double hMm, double x0 = 0) => new([new(x0, 0), new(x0 + wMm, 0), new(x0 + wMm, hMm), new(x0, hMm)]);

    private static DetectorZone Zone(string id, Polygon outline) => new(id, id, outline, new Length(3), DetectorKind.Smoke, DetectionAlgorithm.A, true);

    private static double Dist(PlanPoint a, PlanPoint b) => Math.Sqrt(((a.XMm - b.XMm) * (a.XMm - b.XMm)) + ((a.YMm - b.YMm) * (a.YMm - b.YMm)));

    [Fact]
    public void Manual_detectors_on_walls_cover_every_point_within_30_m()
    {
        // 80 × 20 м без размеченных выходов: правило «не более 30 м от любой точки» (6.6.27) — ИПР на стенах.
        var zone = Zone("R1", Rect(80000, 20000));
        var layout = DetectorLayouter.Layout([zone], Norms);

        var result = AlarmZoning.Build([zone], layout, Norms, manualIsolator: false);

        result.Manuals.Count.ShouldBeGreaterThanOrEqualTo(2);
        result.Manuals.ShouldAllBe(m => zone.Outline.DistanceTo(m.Location) <= 1);
        for (var x = 0.0; x <= 80000; x += 1000)
        {
            for (var y = 0.0; y <= 20000; y += 1000)
            {
                var p = new PlanPoint(x, y);
                result.Manuals.Min(m => Dist(m.Location, p)).ShouldBeLessThanOrEqualTo(30000 + 1);
            }
        }

        result.Manuals.Select(m => m.Id).ShouldBe(result.Manuals.Select((m, i) => $"R1.M{i + 1}"));
        result.Issues.ShouldContain(i => i.Code == "aps.manual.noExits" && i.Level == IssueLevel.Info);
    }

    [Fact]
    public void Large_room_is_split_into_zones_by_area_and_detectors_get_zone_ids()
    {
        // 60 × 40 м = 2400 м² → 2 ЗКСПС; извещатели делятся по длинной стороне.
        var zone = Zone("R1", Rect(60000, 40000));
        var layout = DetectorLayouter.Layout([zone], Norms);

        var result = AlarmZoning.Build([zone], layout, Norms, manualIsolator: false);

        result.ZoneOf.Values.Distinct().Order().ShouldBe(["ЗКСПС-1", "ЗКСПС-2"]);
        var z1 = layout.Detectors.Where(d => result.ZoneOf[d.Id] == "ЗКСПС-1").Select(d => d.Location.XMm).ToList();
        var z2 = layout.Detectors.Where(d => result.ZoneOf[d.Id] == "ЗКСПС-2").Select(d => d.Location.XMm).ToList();
        z1.Max().ShouldBeLessThan(z2.Min());
        (z1.Count + z2.Count).ShouldBe(layout.Detectors.Count);
    }

    [Fact]
    public void Small_rooms_are_one_zone_each_numbered_in_order()
    {
        var zones = new[] { Zone("R1", Rect(20000, 10000)), Zone("R2", Rect(20000, 10000, 30000)) };
        var layout = DetectorLayouter.Layout(zones, Norms);

        var result = AlarmZoning.Build(zones, layout, Norms, manualIsolator: false);

        layout.Detectors.Where(d => d.RoomId == "R1").ShouldAllBe(d => result.ZoneOf[d.Id] == "ЗКСПС-1");
        layout.Detectors.Where(d => d.RoomId == "R2").ShouldAllBe(d => result.ZoneOf[d.Id] == "ЗКСПС-2");
        result.Manuals.Where(m => m.RoomId == "R1").ShouldNotBeEmpty();
    }

    [Fact]
    public void Isolators_one_per_24_automatic_detectors_and_one_per_manual_without_its_own()
    {
        // 60 × 40 м, алгоритм C: много ИП → группы по 24; ИПР без встроенного ИКЗ — по изолятору на каждый.
        var zone = Zone("R1", Rect(60000, 40000)) with { Algorithm = DetectionAlgorithm.C };
        var layout = DetectorLayouter.Layout([zone], Norms);

        var without = AlarmZoning.Build([zone], layout, Norms, manualIsolator: false);
        var with = AlarmZoning.Build([zone], layout, Norms, manualIsolator: true);

        var groups = without.ZoneOf.GroupBy(kv => kv.Value).Sum(g => (g.Count() + 23) / 24);
        without.Isolators.ShouldBe(groups + without.Manuals.Count);
        with.Isolators.ShouldBe(groups);
        without.Issues.ShouldContain(i => i.Code == "aps.isolators" && i.Clause!.Contains("6.3.5"));
    }
}
