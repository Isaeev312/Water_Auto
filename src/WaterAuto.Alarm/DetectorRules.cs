using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>
/// Правила размещения точечных ИП одного вида — из data/norms/sp484 (6.6.15, 6.6.16, 6.6.36, 6.5.1, 6.6.1, 6.6.2).
/// </summary>
/// <param name="Radius">Радиус зоны контроля.</param>
/// <param name="MaxRoomHeight">Наибольшая высота помещения для этого вида ИП.</param>
/// <param name="MinToWall">Отступ от стен и выступающих конструкций.</param>
/// <param name="MinSpacing">Наименьшее расстояние между ИП при алгоритме C.</param>
/// <param name="Coverage">Сколькими ИП контролируется каждая точка.</param>
/// <param name="NormIds">ID записей норм — для журнала расчёта.</param>
public sealed record DetectorRules(Length Radius, Length MaxRoomHeight, Length MinToWall, Length MinSpacing, int Coverage, IReadOnlyList<string> NormIds)
{
    public const string SmokeId = "sp484.p6_6_16.smokePoint";
    public const string HeatId = "sp484.p6_6_15.heatPoint";
    public const string WallId = "sp484.p6_6_36.wallOffset";
    public const string SpacingId = "sp484.p6_5_1.minSpacingAlgorithmC";
    public const string CoverageAbId = "sp484.p6_6_1.coverageAB";
    public const string CoverageCId = "sp484.p6_6_2.coverageC";

    public static string Clause(DetectorKind kind) => kind == DetectorKind.Smoke ? "СП 484.1311500.2020, п. 6.6.16" : "СП 484.1311500.2020, п. 6.6.15";

    /// <summary>Правила для вида ИП, алгоритма и адресности; null с замечанием — если записи норм нет.</summary>
    public static (DetectorRules? Rules, IReadOnlyList<Issue> Issues) Resolve(INormsProvider norms, DetectorKind kind, DetectionAlgorithm algorithm, bool addressable)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        var kindId = kind == DetectorKind.Smoke ? SmokeId : HeatId;
        var coverageId = algorithm == DetectionAlgorithm.C ? CoverageCId : CoverageAbId;
        string[] ids = [kindId, WallId, SpacingId, coverageId];
        var records = ids.ToDictionary(id => id, norms.Find);
        foreach (var (id, record) in records.Where(kv => kv.Value is null))
        {
            issues.Add(new Issue("aps.layout.noNorm", IssueLevel.Error, $"В данных норм нет записи {id} (размещение ИП)."));
        }

        if (issues.Count > 0)
        {
            return (null, issues);
        }

        var k = records[kindId]!;
        var coverage = algorithm == DetectionAlgorithm.C
            ? records[CoverageCId]!.Values["detectors"]
            : records[CoverageAbId]!.Values[addressable ? "addressable" : "nonAddressable"];
        return (new DetectorRules(
            new Length(k.Values["radius"]!.Value),
            new Length(k.Values["maxRoomHeight"]!.Value),
            new Length(records[WallId]!.Values["minToWall"]!.Value),
            new Length(records[SpacingId]!.Values["minSpacing"]!.Value),
            (int)Math.Round(coverage!.Value),
            ids), issues);
    }
}
