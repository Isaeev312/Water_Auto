using System.Text;
using System.Text.RegularExpressions;

namespace WaterAuto.Reports;

/// <summary>Строка реестра литературы `docs/literature_registry.csv`.</summary>
public sealed record LiteratureEntry(string Id, string Title, string Authors, string Year, string Status);

/// <summary>Чтение реестра литературы (CSV, первая строка — заголовок; кавычки по RFC 4180).</summary>
public static class LiteratureRegistry
{
    public static IReadOnlyList<LiteratureEntry> Parse(TextReader csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = Rows(csv.ReadToEnd()).Where(r => r.Count > 1).ToList();
        return rows.Skip(1)
            .Select(r => new LiteratureEntry(Cell(r, 0), Cell(r, 1), Cell(r, 2), Cell(r, 3), Cell(r, 5)))
            .ToList();
    }

    private static string Cell(List<string> row, int i) => i < row.Count ? row[i].Trim() : "";

    private static IEnumerable<List<string>> Rows(string text)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                yield return row;
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            yield return row;
        }
    }
}

/// <summary>
/// Запись списка источников в духе ГОСТ Р 7.0.100 из того, что есть в реестре: обозначение и название, авторы, год.
/// Места издания, издательства и объёма в реестре нет — они помечаются «[дополнить …]», а не выдумываются.
/// </summary>
public static partial class Bibliography
{
    private const string Missing = "[дополнить по ГОСТ Р 7.0.100: место издания, издательство, объём]";

    public static string Format(LiteratureEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var norm = NormTitle().Match(entry.Title);
        if (norm.Success)
        {
            var code = norm.Groups["code"].Value.Trim();
            var name = norm.Groups["name"].Value.Trim();
            var rest = norm.Groups["rest"].Value.Trim();
            var kind = code.StartsWith("ГОСТ", StringComparison.Ordinal) ? "стандарт" : "свод правил";
            var edition = rest.Length > 0 ? $" : {rest}" : "";
            return $"{code}. {name} : {kind}{edition}. — {Year(entry.Year)}. — {Missing}.";
        }

        var authors = entry.Authors is "" or "—" ? "" : $"{entry.Authors.TrimEnd('.')}. ";
        return $"{authors}{entry.Title.TrimEnd('.')}. — {Year(entry.Year)}. — {Missing}.";
    }

    /// <summary>Сначала нормативные документы, затем остальное по алфавиту (авторы, затем название).</summary>
    public static IReadOnlyList<LiteratureEntry> Order(IEnumerable<LiteratureEntry> entries) =>
        entries
            .OrderBy(e => NormTitle().IsMatch(e.Title) ? 0 : 1)
            .ThenBy(e => e.Authors is "" or "—" ? e.Title : e.Authors, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), false))
            .ToList();

    private static string Year(string year) => year.Length > 0 ? year : "[год]";

    // «СП 485.1311500.2020 «Название» остаток» — обозначение, название в кавычках-ёлочках и редакция.
    [GeneratedRegex(@"^(?<code>(СП|ГОСТ( Р)?|СН КР|СНиП|НПБ|ВСН)\s[^«]+?)\s*«(?<name>[^»]+)»\s*(?<rest>.*)$")]
    private static partial Regex NormTitle();
}
