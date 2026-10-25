namespace WaterAuto.Core;

/// <summary>Запись норм из data/norms (README §6.1). Values: null — значения в ячейке нет.</summary>
public sealed record NormRecord(
    string Id,
    string Doc,
    string Clause,
    IReadOnlyDictionary<string, double?> Values,
    IReadOnlyDictionary<string, string> Units,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>Доступ к нормам как к данным (ARCHITECTURE §3). Реализация — WaterAuto.Norms.</summary>
public interface INormsProvider
{
    NormRecord? Find(string id);

    IEnumerable<NormRecord> ByClause(string clause);
}
