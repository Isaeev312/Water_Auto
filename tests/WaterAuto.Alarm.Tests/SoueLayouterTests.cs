using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// СОУЭ (СП 3.13130.2026): требуемый уровень звука по фону (6.8.2), радиус по В.2, сетка оповещателей, световые по 6.16.2.
public class SoueLayouterTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static Polygon Rect(double wMm, double hMm) => new([new(0, 0), new(wMm, 0), new(wMm, hMm), new(0, hMm)]);

    // Звуковой оповещатель 105 дБ на 1 м.
    private static readonly NotifierModel Sounder = new("Маяк-12-3М", NotifierKind.Sound, 105, new Current(0.03), "РЭ", null);
    private static readonly NotifierModel Light = new("Молния-12 «Выход»", NotifierKind.Light, null, new Current(0.02), "РЭ", null);

    private static SoueZone Zone(Polygon outline, double background = 70, WarningMethod method = WarningMethod.Sound) =>
        new("R1", "Автостоянка", outline, background, method);

    [Fact]
    public void Required_level_is_background_plus_15_but_not_below_65_and_radius_follows_B2()
    {
        // Фон 70 → требуется 85 дБА; 105 дБ на 1 м → r = 10^((105−85)/20) = 10 м.
        var result = SoueLayouter.Layout([Zone(Rect(20000, 10000))], Sounder, Light, Norms);

        var z = result.Zones.Single();
        z.RequiredLevel.ShouldBe(85, 1e-9);
        z.SoundRadius.Meters.ShouldBe(10, 1e-9);
        // Шаг сетки r·√2 = 14,1 м → 20 × 10 м: 2 × 1 = 2 оповещателя.
        result.Sounders.Count(s => s.RoomId == "R1").ShouldBe(2);

        // Фон 40 (квартира не считаем) → 40 + 15 = 55 < 65 → 65 дБА.
        SoueLayouter.Layout([Zone(Rect(20000, 10000), background: 40)], Sounder, Light, Norms).Zones.Single().RequiredLevel.ShouldBe(65, 1e-9);
    }

    [Fact]
    public void Loud_background_needs_105_dBA_and_light_notification()
    {
        var result = SoueLayouter.Layout([Zone(Rect(20000, 10000), background: 90)], Sounder, Light, Norms);

        result.Zones.Single().RequiredLevel.ShouldBe(105, 1e-9);
        // 105 на 1 м при требуемых 105 → r = 1 м: модель непригодна — ошибка, оповещатели не расставляются.
        result.Issues.ShouldContain(i => i.Code == "soue.sounder.weak" && i.Level == IssueLevel.Error && i.Message.Contains("119 дБ"));
        result.Sounders.ShouldBeEmpty();
        result.Issues.ShouldContain(i => i.Code == "soue.light.required" && i.Clause!.Contains("6.8.2"));
    }

    [Fact]
    public void Speech_method_limits_spacing_to_6_m_and_background_to_65()
    {
        var result = SoueLayouter.Layout([Zone(Rect(20000, 10000), background: 70, method: WarningMethod.Speech)], Sounder with { Kind = NotifierKind.Speech }, Light, Norms);

        // Фон 70 > 65 — речевое оповещение неприменимо (6.11.3).
        result.Issues.ShouldContain(i => i.Code == "soue.speech.background" && i.Level == IssueLevel.Error);

        var ok = SoueLayouter.Layout([Zone(Rect(20000, 10000), background: 60, method: WarningMethod.Speech)], Sounder with { Kind = NotifierKind.Speech }, Light, Norms);
        // Требуется 60 + 10 = 70 → r = 10^(35/20) = 56 м, но шаг ≤ 6 м: 20 × 10 м → 4 × 2 = 8 оповещателей.
        ok.Sounders.Count.ShouldBe(8);
    }

    [Fact]
    public void Light_notifiers_cover_every_point_within_15_m_from_walls()
    {
        var zone = Zone(Rect(60000, 20000));
        var result = SoueLayouter.Layout([zone], Sounder, Light, Norms);

        result.Lights.ShouldNotBeEmpty();
        result.Lights.ShouldAllBe(l => zone.Outline.DistanceTo(l.Location) <= 1);
        for (var x = 0.0; x <= 60000; x += 1000)
        {
            for (var y = 0.0; y <= 20000; y += 1000)
            {
                var p = new PlanPoint(x, y);
                result.Lights.Min(l => Math.Sqrt(Math.Pow(l.Location.XMm - p.XMm, 2) + Math.Pow(l.Location.YMm - p.YMm, 2))).ShouldBeLessThanOrEqualTo(15000 + 1);
            }
        }
    }

    [Fact]
    public void Large_zone_is_reported_against_the_2000_m2_single_fault_limit()
    {
        var result = SoueLayouter.Layout([Zone(Rect(60000, 40000))], Sounder, Light, Norms);

        result.Issues.ShouldContain(i => i.Code == "soue.zone.split" && i.Clause!.Contains("6.4.1") && i.Message.Contains("2 "));
    }

    [Fact]
    public void Sounders_are_placed_on_the_ceiling_grid_and_mount_height_is_noted()
    {
        var result = SoueLayouter.Layout([Zone(Rect(20000, 10000))], Sounder, Light, Norms);

        result.Sounders.Select(s => s.Id).ShouldBe(["R1.S1", "R1.S2"]);
        result.Issues.ShouldContain(i => i.Code == "soue.mountHeight" && i.Message.Contains("2,3 м"));
    }
}
