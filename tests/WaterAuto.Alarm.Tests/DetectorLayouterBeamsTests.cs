using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// Балки (СП 484, табл. 6.2): ИП — в полях между балками, шаг поперёк балок и расстояние до граней — из таблицы.
public class DetectorLayouterBeamsTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static Polygon Rect(double x0, double y0, double x1, double y1) => new([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)]);

    // 20 × 10 м, две балки поперёк X (вдоль Y) шириной 0,5 м на x = 6,25…6,75 и 13,25…13,75 м.
    private static DetectorZone Zone(double beamHeightM, double roomHeightM = 3) => new("R1", "Стоянка", Rect(0, 0, 20000, 10000), new Length(roomHeightM), DetectorKind.Smoke, DetectionAlgorithm.A, true)
    {
        Beams =
        [
            new Beam("B1", Rect(6250, 0, 6750, 10000), new Length(beamHeightM), new Length(roomHeightM)),
            new Beam("B2", Rect(13250, 0, 13750, 10000), new Length(beamHeightM), new Length(roomHeightM)),
        ],
    };

    [Fact]
    public void High_beams_bound_the_grid_across_them_by_table_6_2()
    {
        // D = 0,5 м > 10 % H → строка H ≤ 3 м: поперёк балок ≤ 2,30 м, до грани ≤ 1,15 м. Поля ~6,25 м → по 3 ИП поперёк; вдоль — 2 ряда.
        var zone = Zone(0.5);

        var layout = DetectorLayouter.Layout([zone], Norms);

        layout.Detectors.Count.ShouldBe(18);
        layout.Detectors.ShouldAllBe(d => zone.Beams.All(b => !b.Outline.Contains(d.Location)));
        foreach (var field in new[] { (0.0, 6250.0), (6750.0, 13250.0), (13750.0, 20000.0) })
        {
            var xs = layout.Detectors.Select(d => d.Location.XMm).Where(x => x > field.Item1 - 1 && x < field.Item2 + 1).Distinct().OrderBy(x => x).ToList();
            xs.Count.ShouldBe(3);
            (xs[0] - field.Item1).ShouldBeLessThanOrEqualTo(1150 + 1);
            (field.Item2 - xs[^1]).ShouldBeLessThanOrEqualTo(1150 + 1);
            xs.Zip(xs.Skip(1)).ShouldAllBe(p => p.Second - p.First <= 2300 + 1);
        }

        layout.Issues.ShouldContain(i => i.Code == "aps.layout.beams" && i.Level == IssueLevel.Info && i.Clause!.Contains("табл. 6.2"));
    }

    [Fact]
    public void Low_beams_still_use_the_table_row_for_any_height()
    {
        // D = 0,2 м < 10 % H → строка «любая высота»: поперёк ≤ 5,00 м, до грани ≤ 2,50 м → по 2 ИП поперёк в поле 6,25 м → 2 × 2 × 3 = 12.
        var layout = DetectorLayouter.Layout([Zone(0.2)], Norms);

        layout.Detectors.Count.ShouldBe(12);
    }

    [Fact]
    public void Crossing_beams_are_reported_as_coffers_and_laid_out_flat()
    {
        var zone = Zone(0.5) with
        {
            Beams = [new Beam("B1", Rect(6250, 0, 6750, 10000), new Length(0.5), new Length(3)), new Beam("B3", Rect(0, 4750, 20000, 5250), new Length(0.5), new Length(3))],
        };

        var layout = DetectorLayouter.Layout([zone], Norms);

        layout.Issues.ShouldContain(i => i.Code == "aps.layout.coffers" && i.Level == IssueLevel.Warning && i.Clause!.Contains("табл. 6.3"));
        layout.Detectors.Count.ShouldBe(6);
    }

    [Fact]
    public void Height_bucket_follows_rounded_ceiling_height()
    {
        // H = 4,4 м → округлённо 4 → строка H = 4: поперёк ≤ 2,80 м → 3 ИП в поле 6,25 м; H = 5,6 → 6 и более: ≤ 3,30 м → 2 ИП.
        DetectorLayouter.Layout([Zone(0.6, roomHeightM: 4.4)], Norms).Detectors.Count.ShouldBe(18);
        DetectorLayouter.Layout([Zone(0.8, roomHeightM: 5.6)], Norms).Detectors.Count.ShouldBe(12);
    }
}
