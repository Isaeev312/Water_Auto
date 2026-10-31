using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// Раскладка точечных ИП (ADR 0012, этап 2) по данным data/norms/sp484: радиусы зон контроля, кратность контроля, отступы.
public class DetectorLayouterTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static Polygon Rect(double wMm, double hMm, double x0 = 0, double y0 = 0) =>
        new([new(x0, y0), new(x0 + wMm, y0), new(x0 + wMm, y0 + hMm), new(x0, y0 + hMm)]);

    private static DetectorZone Zone(Polygon outline, double heightM = 3, DetectorKind kind = DetectorKind.Smoke, DetectionAlgorithm alg = DetectionAlgorithm.A, bool addressable = true) =>
        new("R1", "Стоянка", outline, new Length(heightM), kind, alg, addressable);

    // Сколько ИП видят каждую точку сетки 250 мм внутри контура.
    private static int MinCoverage(Polygon outline, IReadOnlyList<PlacedDetector> detectors, double radiusMm)
    {
        var xs = outline.Vertices.Select(v => v.XMm); var ys = outline.Vertices.Select(v => v.YMm);
        var min = int.MaxValue;
        for (var x = xs.Min(); x <= xs.Max(); x += 250)
        {
            for (var y = ys.Min(); y <= ys.Max(); y += 250)
            {
                var p = new PlanPoint(x, y);
                if (!outline.Contains(p)) continue;
                var n = detectors.Count(d => Dist(d.Location, p) <= radiusMm + 1);
                min = Math.Min(min, n);
            }
        }

        return min;
    }

    private static double Dist(PlanPoint a, PlanPoint b) => Math.Sqrt(((a.XMm - b.XMm) * (a.XMm - b.XMm)) + ((a.YMm - b.YMm) * (a.YMm - b.YMm)));

    [Fact]
    public void Addressable_smoke_detectors_cover_a_rectangle_once_on_a_regular_grid()
    {
        // 20 × 10 м, R = 6,4 м: шаг сетки R·√2 = 9,05 м → 3 × 2 = 6 ИП.
        var zone = Zone(Rect(20000, 10000));

        var layout = DetectorLayouter.Layout([zone], Norms);

        layout.Detectors.Count.ShouldBe(6);
        layout.CountByRoom["R1"].ShouldBe(6);
        layout.Detectors.Select(d => d.Id).ShouldBe(["R1.D1", "R1.D2", "R1.D3", "R1.D4", "R1.D5", "R1.D6"]);
        MinCoverage(zone.Outline, layout.Detectors, 6400).ShouldBeGreaterThanOrEqualTo(1);
        layout.Detectors.ShouldAllBe(d => zone.Outline.DistanceTo(d.Location) >= 500);
        layout.Issues.ShouldNotContain(i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Algorithm_C_covers_every_point_twice()
    {
        var zone = Zone(Rect(20000, 10000), alg: DetectionAlgorithm.C);

        var layout = DetectorLayouter.Layout([zone], Norms);

        MinCoverage(zone.Outline, layout.Detectors, 6400).ShouldBeGreaterThanOrEqualTo(2);
        layout.Detectors.Count.ShouldBeGreaterThan(6);
        // Между ИП — не меньше 1,2 м (6.5.1).
        foreach (var a in layout.Detectors)
        {
            layout.Detectors.Where(b => b != a).ShouldAllBe(b => Dist(a.Location, b.Location) >= 1200 - 1);
        }
    }

    [Fact]
    public void Non_addressable_A_or_B_also_needs_double_coverage_but_addressable_does_not()
    {
        var twice = DetectorLayouter.Layout([Zone(Rect(20000, 10000), alg: DetectionAlgorithm.B, addressable: false)], Norms);
        var once = DetectorLayouter.Layout([Zone(Rect(20000, 10000), alg: DetectionAlgorithm.B, addressable: true)], Norms);

        twice.Detectors.Count.ShouldBeGreaterThan(once.Detectors.Count);
    }

    [Fact]
    public void Heat_detectors_use_their_own_radius()
    {
        // R = 3,5 м: шаг 4,95 м → 20 × 10 м = 5 × 3 = 15 ИП.
        var layout = DetectorLayouter.Layout([Zone(Rect(20000, 10000), kind: DetectorKind.Heat)], Norms);

        layout.Detectors.Count.ShouldBe(15);
    }

    [Fact]
    public void Room_higher_than_the_detector_limit_is_an_error_without_detectors()
    {
        var layout = DetectorLayouter.Layout([Zone(Rect(20000, 10000), heightM: 10, kind: DetectorKind.Heat)], Norms);

        layout.Detectors.ShouldBeEmpty();
        layout.Issues.ShouldContain(i => i.Code == "aps.layout.height" && i.Level == IssueLevel.Error && i.Clause!.Contains("6.6.15"));
    }

    [Fact]
    public void Non_rectangular_room_is_reported_and_skipped()
    {
        var l = new Polygon([new(0, 0), new(20000, 0), new(20000, 5000), new(10000, 5000), new(10000, 10000), new(0, 10000)]);

        var layout = DetectorLayouter.Layout([Zone(l)], Norms);

        layout.Detectors.ShouldBeEmpty();
        layout.Issues.ShouldContain(i => i.Code == "aps.layout.nonRectangular" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Narrow_room_gets_detectors_centred_between_walls_with_a_note()
    {
        // 0,8 м ширины: 0,5 м от стен не выдержать — ИП по центру (6.6.36), об этом Info.
        var layout = DetectorLayouter.Layout([Zone(Rect(6000, 800))], Norms);

        layout.Detectors.ShouldAllBe(d => Math.Abs(d.Location.YMm - 400) < 1);
        layout.Issues.ShouldContain(i => i.Code == "aps.layout.wallOffset" && i.Level == IssueLevel.Info);
    }

    [Fact]
    public void Ids_restart_per_room_and_counts_are_per_room()
    {
        var layout = DetectorLayouter.Layout(
        [
            Zone(Rect(5000, 5000)),
            new DetectorZone("R2", "Склад", Rect(5000, 5000, 10000, 0), new Length(3), DetectorKind.Smoke, DetectionAlgorithm.A, true),
        ], Norms);

        layout.CountByRoom.ShouldBe(new Dictionary<string, int> { ["R1"] = 1, ["R2"] = 1 });
        layout.Detectors.Select(d => d.Id).ShouldBe(["R1.D1", "R2.D1"]);
    }
}
