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

    /// <summary>
    /// Табл. 6.2: наибольшее расстояние поперёк балок между ИП и от ИП до стены (грани балки). Строка — по доле высоты
    /// балки от высоты перекрытия (менее 10 % — одна строка для любой высоты) и по высоте перекрытия, округлённой до целого.
    /// </summary>
    public static (Length Across, Length ToWall, string RecordId)? BeamSpacing(INormsProvider norms, DetectorKind kind, Length ceilingHeight, Length beamHeight)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var low = beamHeight.Meters < 0.1 * ceilingHeight.Meters;
        var h = Math.Round(ceilingHeight.Meters, MidpointRounding.AwayFromZero);
        var rows = norms.ByClause("табл. 6.2").Where(r => r.Doc == "СП 484.1311500.2020" && r.Attributes.GetValueOrDefault("beam") == (low ? "lt10" : "gt10"));
        var row = rows.FirstOrDefault(r =>
            (r.Values.GetValueOrDefault("hMin") is not { } min || h > min) && (r.Values.GetValueOrDefault("hMax") is not { } max || h <= max));
        if (row is null)
        {
            return null;
        }

        var prefix = kind == DetectorKind.Smoke ? "smoke" : "heat";
        return (new Length(row.Values[prefix]!.Value), new Length(row.Values[prefix + "ToWall"]!.Value), row.Id);
    }

    /// <summary>
    /// Табл. 6.3 (кессоны — балки двух направлений): наибольшее расстояние до ближайшего ИП и место установки. Null — балки
    /// ниже 10 % высоты, размещение как при плоском потолке. OnBeams — при ширине ячейки W ≤ 4D ИП ставят на нижней плоскости балок.
    /// </summary>
    public static (Length Distance, string RecordId)? CofferSpacing(INormsProvider norms, DetectorKind kind, Length ceilingHeight, Length beamHeight)
    {
        ArgumentNullException.ThrowIfNull(norms);
        if (beamHeight.Meters < 0.1 * ceilingHeight.Meters)
        {
            return null;
        }

        var h = Math.Round(ceilingHeight.Meters, MidpointRounding.AwayFromZero);
        var row = norms.ByClause("табл. 6.3").Where(r => r.Doc == "СП 484.1311500.2020" && r.Attributes.GetValueOrDefault("beam") == "gt10")
            .FirstOrDefault(r => (r.Values.GetValueOrDefault("hMin") is not { } min || h > min) && (r.Values.GetValueOrDefault("hMax") is not { } max || h <= max));
        return row is null ? null : (new Length(row.Values[kind == DetectorKind.Smoke ? "smoke" : "heat"]!.Value), row.Id);
    }

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
