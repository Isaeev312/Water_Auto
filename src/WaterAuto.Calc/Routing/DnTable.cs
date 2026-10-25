using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>
/// Начальный DN участка по числу оросителей за ним — табл. Б.4 (Б.1.3.2: ориентировочно, уточняется гидравлическим расчётом).
/// Столбец таблицы выбирается по расчётному давлению: не ниже порога из данных или ниже.
/// </summary>
public sealed class DnTable
{
    private readonly List<(int Dn, double? Max)> _rows;
    private readonly int _openEndedDn;

    private DnTable(List<(int Dn, double? Max)> rows, int openEndedDn, IReadOnlyList<string> normIds) =>
        (_rows, _openEndedDn, NormIds) = (rows, openEndedDn, normIds);

    public IReadOnlyList<string> NormIds { get; }

    public static DnTable? Resolve(INormsProvider norms, Pressure designPressure)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var threshold = norms.Find("sp485.tB_4.pressureThreshold")?.Values["threshold"];
        var records = norms.ByClause("табл. Б.4").Where(r => r.Values.ContainsKey("dn")).OrderBy(r => r.Values["dn"]).ToList();
        var open = records.FirstOrDefault(r => r.Attributes.GetValueOrDefault("openEnded") == "true");
        if (threshold is null || records.Count == 0 || open is null)
        {
            return null;
        }

        var key = designPressure.Mpa >= threshold.Value - 1e-12 ? "atOrAbovePressure" : "belowPressure";
        var rows = records.Where(r => r != open).Select(r => ((int)r.Values["dn"]!.Value, r.Values[key])).ToList();
        return new DnTable(rows, (int)open.Values["dn"]!.Value, [.. records.Select(r => r.Id), "sp485.tB_4.pressureThreshold"]);
    }

    /// <summary>Наименьший DN, на котором допускается <paramref name="fed"/> оросителей; сверх таблицы — открытая строка («более …»).</summary>
    public int Pick(int fed) =>
        _rows.Where(r => r.Max is { } max && fed <= max + 1e-9).Select(r => r.Dn).DefaultIfEmpty(_openEndedDn).First();
}
