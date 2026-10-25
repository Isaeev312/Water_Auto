using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>Труба сортамента: строка табл. Б.2 и DN, которым она принята.</summary>
public sealed record SeriesPipe(int Dn, double OuterDiameterMm, double WallMm, PipeCharacteristic Kt, string PipeType, string NormId);

/// <summary>
/// Сортамент стандарта трубы из табл. Б.2 (без строк «только наружные сети»). DN, которого нет в сортаменте
/// (DN 70 из табл. Б.4), заменяется ближайшим большим (В-19); несколько труб одного DN — берётся первая, с замечанием.
/// Замечания выдаются по одному на DN.
/// </summary>
public sealed class PipeSeries
{
    private readonly string _standard;
    private readonly List<NormRecord> _rows;
    private readonly Dictionary<int, SeriesPipe?> _cache = [];
    private readonly List<Issue> _issues = [];

    public PipeSeries(INormsProvider norms, string standard)
    {
        ArgumentNullException.ThrowIfNull(norms);
        _standard = standard;
        _rows = norms.ByClause("табл. Б.2")
            .Where(r => r.Attributes.GetValueOrDefault("standard") == standard && r.Attributes.GetValueOrDefault("outdoorOnly") != "true")
            .OrderBy(r => r.Values["dn"])
            .ToList();
    }

    public IReadOnlyList<Issue> Issues => _issues;

    public SeriesPipe? Pick(int dn)
    {
        if (_cache.TryGetValue(dn, out var known))
        {
            return known;
        }

        var actual = _rows.Select(r => (int)r.Values["dn"]!.Value).Where(d => d >= dn).DefaultIfEmpty(-1).Min();
        if (actual < 0)
        {
            _issues.Add(new Issue("network.noPipe", IssueLevel.Error, $"В табл. Б.2 для {_standard} нет трубы DN {dn} и больше.", Clause: "табл. Б.2"));
            return _cache[dn] = null;
        }

        var rows = _rows.Where(r => (int)r.Values["dn"]!.Value == actual).ToList();
        var row = rows[0];
        if (actual != dn)
        {
            _issues.Add(new Issue("network.dnSubstituted", IssueLevel.Info, $"DN {dn} (табл. Б.4) нет в сортаменте {_standard} — принят DN {actual}.", row.Id, "табл. Б.4"));
        }

        if (rows.Count > 1)
        {
            _issues.Add(new Issue("network.pipeAmbiguous", IssueLevel.Warning, $"Для {_standard} DN {actual} в табл. Б.2 несколько труб — принята {row.Id}; задайте трубу явно.", row.Id, "табл. Б.2"));
        }

        return _cache[dn] = new SeriesPipe(
            actual,
            row.Values["D_out"]!.Value,
            row.Values["wall"]!.Value,
            new PipeCharacteristic(row.Values["Kt"]!.Value),
            row.Attributes.GetValueOrDefault("pipeType") ?? "",
            row.Id);
    }
}
