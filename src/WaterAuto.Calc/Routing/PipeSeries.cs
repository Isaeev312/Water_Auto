using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>Труба сортамента: строка табл. Б.2 и DN, которым она принята.</summary>
/// <summary>Принятый размер трубы для DN (практика проекта): наружный диаметр × стенка; Kт — табл. Б.2 или формула Б.8.</summary>
public sealed record PipeSize(string Standard, int Dn, double OuterDiameterMm, double WallMm);

public sealed record SeriesPipe(int Dn, double OuterDiameterMm, double WallMm, PipeCharacteristic Kt, string PipeType, string NormId, string Standard = "");

/// <summary>
/// Сортамент стандарта трубы из табл. Б.2 (без строк «только наружные сети»). DN, которого нет в сортаменте
/// (DN 70 из табл. Б.4), заменяется ближайшим большим (В-19); несколько труб одного DN — берётся первая, с замечанием.
/// Замечания выдаются по одному на DN. Стандарт можно задать с годом («ГОСТ 3262-75») и составным
/// (<see cref="Compose"/>): малые DN по одному стандарту, начиная с заданного DN — по другому.
/// </summary>
public sealed class PipeSeries
{
    private readonly string _standard;
    private readonly List<NormRecord> _rows;
    private readonly Dictionary<int, SeriesPipe?> _cache = [];
    private readonly List<Issue> _issues = [];

    private readonly INormsProvider _norms;
    private readonly IReadOnlyList<PipeSize> _sizes;

    /// <param name="sizes">Принятые размеры труб по DN; для DN из списка вместо первой строки табл. Б.2 берётся этот размер.</param>
    public PipeSeries(INormsProvider norms, string standard, IReadOnlyList<PipeSize>? sizes = null)
    {
        ArgumentNullException.ThrowIfNull(norms);
        _norms = norms;
        _sizes = sizes ?? [];
        _standard = standard;
        var parts = Parse(standard);
        _rows = norms.ByClause("табл. Б.2")
            .Where(r => r.Attributes.GetValueOrDefault("outdoorOnly") != "true")
            .Where(r => parts.Any(p => Matches(p.Standard, r) && r.Values["dn"]!.Value >= p.FromDn && r.Values["dn"]!.Value < p.ToDn))
            .OrderBy(r => r.Values["dn"])
            .ToList();
        _designation = r => parts.First(p => Matches(p.Standard, r) && r.Values["dn"]!.Value >= p.FromDn && r.Values["dn"]!.Value < p.ToDn).Standard;
    }

    private readonly Func<NormRecord, string> _designation;

    private const string LargeMark = " от DN";

    /// <summary>Составной сортамент: до <paramref name="fromDn"/> — <paramref name="small"/>, начиная с него — <paramref name="large"/>.</summary>
    public static string Compose(string small, string large, int fromDn) => $"{small}; {large}{LargeMark}{fromDn}";

    private static bool Matches(string designation, NormRecord row) =>
        row.Attributes.GetValueOrDefault("standard") is { } s && (designation == s || designation.StartsWith(s + "-", StringComparison.Ordinal));

    private static List<(string Standard, double FromDn, double ToDn)> Parse(string standard)
    {
        var split = standard.Split("; ", 2);
        if (split.Length == 2 && split[1].LastIndexOf(LargeMark, StringComparison.Ordinal) is var at and > 0
            && int.TryParse(split[1].AsSpan(at + LargeMark.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var from))
        {
            return [(split[0], 0, from), (split[1][..at], from, double.MaxValue)];
        }

        return [(standard, 0, double.MaxValue)];
    }

    public IReadOnlyList<Issue> Issues => _issues;

    /// <summary>Наименьшая труба сортамента не меньше DN <paramref name="minDn"/> с внутренним диаметром не меньше заданного, мм.</summary>
    public SeriesPipe? WithInnerAtLeast(double innerMm, int minDn)
    {
        foreach (var dn in _rows.Select(r => (int)r.Values["dn"]!.Value).Where(d => d >= minDn).Distinct())
        {
            if (Pick(dn) is { } pipe && pipe.OuterDiameterMm - (2 * pipe.WallMm) >= innerMm - 1e-9)
            {
                return pipe;
            }
        }

        return null;
    }

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
        var designation = _designation(row);
        var baseStandard = row.Attributes.GetValueOrDefault("standard") ?? "";
        if (_sizes.FirstOrDefault(s => s.Dn == actual && s.Standard == baseStandard) is { } size)
        {
            // Принятый размер: Kт по табл. Б.2, если там есть такая труба, иначе по формуле Б.8 (п. Б.1.2.9).
            var kt = Hydraulics.PipeCharacteristics.Resolve(_norms, baseStandard, actual, new Length(size.OuterDiameterMm / 1000), new Length(size.WallMm / 1000));
            if (kt is not null)
            {
                return _cache[dn] = new SeriesPipe(actual, size.OuterDiameterMm, size.WallMm, kt.Value, row.Attributes.GetValueOrDefault("pipeType") ?? "", kt.NormId, designation);
            }

            _issues.Add(new Issue("network.pipeSize", IssueLevel.Warning, string.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), $"Принятый размер {size.OuterDiameterMm}×{size.WallMm} мм для DN {actual} нельзя рассчитать (нет в табл. Б.2 и вне формулы Б.8) — взята труба табл. Б.2."), row.Id, "Б.1.2.9"));
        }

        if (actual != dn)
        {
            _issues.Add(new Issue("network.dnSubstituted", IssueLevel.Info, $"DN {dn} (табл. Б.4) нет в сортаменте {_standard} — принят DN {actual}.", row.Id, "табл. Б.4"));
        }

        if (rows.Count > 1 && !_sizes.Any(s => s.Dn == actual && s.Standard == baseStandard))
        {
            _issues.Add(new Issue("network.pipeAmbiguous", IssueLevel.Warning, $"Для {_standard} DN {actual} в табл. Б.2 несколько труб — принята {row.Id}; задайте трубу явно.", row.Id, "табл. Б.2"));
        }

        return _cache[dn] = new SeriesPipe(
            actual,
            row.Values["D_out"]!.Value,
            row.Values["wall"]!.Value,
            new PipeCharacteristic(row.Values["Kt"]!.Value),
            row.Attributes.GetValueOrDefault("pipeType") ?? "",
            row.Id,
            _designation(row));
    }
}
