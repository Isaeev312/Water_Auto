using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>
/// Проверки состава СПС после раскладки (СП 484): помещение больше одной ЗКСПС (6.3.4), ёмкость ППКП по числу ИП и площади
/// (6.1.5), запас ёмкости (5.20). Деление на ЗКСПС и выбор ППКП — решения проектировщика, программа только предупреждает.
/// </summary>
public static class ZoneChecks
{
    public const string ZoneId = "sp484.p6_3_4.zone";
    public const string PanelId = "sp484.p6_1_5.controlPanel";
    public const string ReserveId = "sp484.p5_20.capacityReserve";

    private const string Doc = "СП 484.1311500.2020";

    public static IReadOnlyList<Issue> Check(IReadOnlyList<DetectorZone> zones, DetectorLayout layout, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(norms);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var issues = new List<Issue>();
        var zone = norms.Find(ZoneId);
        var panel = norms.Find(PanelId);
        var reserve = norms.Find(ReserveId);
        foreach (var (id, record) in new[] { (ZoneId, zone), (PanelId, panel), (ReserveId, reserve) }.Where(x => x.Item2 is null))
        {
            issues.Add(new Issue("aps.zone.noNorm", IssueLevel.Error, $"В данных норм нет записи {id} (ЗКСПС, ёмкость ППКП)."));
        }

        if (zone is null || panel is null || reserve is null)
        {
            return issues;
        }

        var maxZoneArea = zone.Values["maxArea"]!.Value;
        foreach (var z in zones)
        {
            var area = z.Outline.AreaMm2 / 1e6;
            if (area > maxZoneArea + 1e-9)
            {
                var parts = (int)Math.Ceiling(area / maxZoneArea);
                issues.Add(new Issue("aps.zone.split", IssueLevel.Warning,
                    string.Create(ru, $"Помещение «{z.RoomName}» площадью {area:0} м² больше одной ЗКСПС ({maxZoneArea:0} м²): нужно не менее {parts} ЗКСПС — деление задаёт проектировщик."),
                    z.RoomId, Doc + ", п. 6.3.4", z.Outline.Centroid));
            }
        }

        var detectors = layout.Detectors.Count;
        var totalArea = zones.Sum(z => z.Outline.AreaMm2) / 1e6;
        var maxDetectors = panel.Values["maxDetectors"]!.Value;
        var maxArea = panel.Values["maxArea"]!.Value;
        if (detectors > maxDetectors)
        {
            issues.Add(new Issue("aps.panel.detectors", IssueLevel.Warning,
                string.Create(ru, $"Извещателей {detectors} — больше {maxDetectors:0} на один ППКП: нужен второй прибор или ППКП с защитой от системной ошибки."),
                Clause: Doc + ", п. 6.1.5"));
        }
        else if (detectors > 0)
        {
            var reservePercent = (maxDetectors - detectors) / maxDetectors * 100;
            var required = reserve.Values["knownLayout"]!.Value;
            if (reservePercent < required - 1e-9)
            {
                issues.Add(new Issue("aps.panel.reserve", IssueLevel.Info,
                    string.Create(ru, $"Запас ёмкости ППКП по числу ИП {reservePercent:0.#} % — меньше {required:0} % при известной планировке; при неизвестной нужно {reserve.Values["unknownLayout"]!.Value:0} %."),
                    Clause: Doc + ", п. 5.20"));
            }
        }

        if (totalArea > maxArea + 1e-9)
        {
            issues.Add(new Issue("aps.panel.area", IssueLevel.Warning,
                string.Create(ru, $"Контролируемая площадь {totalArea:0} м² — больше {maxArea:0} м² на один ППКП."),
                Clause: Doc + ", п. 6.1.5"));
        }

        return issues;
    }
}
