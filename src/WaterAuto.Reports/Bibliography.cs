using System.Text;
using System.Text.RegularExpressions;

namespace WaterAuto.Reports;

/// <summary>
/// Строка реестра литературы `docs/literature_registry.csv`: библиографические данные для списка источников
/// (ГОСТ Р 7.0.100): обозначение/название, авторы, год, место издания, издательство, объём.
/// </summary>
public sealed record LiteratureEntry(
    string Id,
    string Title,
    string Authors,
    string Year,
    string Status,
    string Place = "",
    string Publisher = "",
    string Volume = "");

/// <summary>Чтение реестра литературы (CSV, первая строка — заголовок; кавычки по RFC 4180).</summary>
public static class LiteratureRegistry
{
    public static IReadOnlyList<LiteratureEntry> Parse(TextReader csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = Rows(csv.ReadToEnd()).Where(r => r.Count > 1).ToList();
        return rows.Skip(1)
            .Select(r => new LiteratureEntry(
                Cell(r, 0), Cell(r, 1), Cell(r, 2), Cell(r, 3), Cell(r, 5),
                Cell(r, 9), Cell(r, 10), Cell(r, 11)))
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
/// Запись списка использованных источников по ГОСТ Р 7.0.100-2018: обозначение/авторы, название,
/// место издания, издательство, год, объём. Чего нет в реестре — помечается в квадратных скобках
/// и не выдумывается; правовые акты описываются датой, номером и названием (место издания к ним не применяется).
/// </summary>
public static partial class Bibliography
{
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
            return $"{code}. {name} : {kind}{edition}. — {Publication(entry)}.";
        }

        if (IsLegalAct(entry.Title))
        {
            // Правовой акт: дата, номер, название, год; место издания и издательство к нему не применяются,
            // объёма у акта нет — список не должен показывать для него «[дополнить]».
            return $"{entry.Title.TrimEnd('.')}. — {Year(entry.Year)}.";
        }

        var authors = entry.Authors is "" or "—" ? "" : $"{entry.Authors.TrimEnd('.')}. ";
        return $"{authors}{entry.Title.TrimEnd('.')}. — {Publication(entry)}.";
    }

    // «Место : издательство, год. — объём с.»; отсутствующие части помечены, а не выдуманы.
    private static string Publication(LiteratureEntry e)
    {
        var place = e.Place.Length > 0 ? e.Place : "[место издания]";
        var publisher = e.Publisher.Length > 0 ? e.Publisher : "[издательство]";
        var year = Year(e.Year);
        var volume = e.Volume.Length > 0 ? $"{e.Volume} с." : "[объём]";
        return $"{place} : {publisher}, {year}. — {volume}";
    }

    private static bool IsLegalAct(string title) =>
        title.StartsWith("Федеральный закон", StringComparison.Ordinal)
        || title.StartsWith("Постановление", StringComparison.Ordinal)
        || title.StartsWith("Приказ", StringComparison.Ordinal);

    /// <summary>
    /// По юридической силе: федеральные законы, постановления правительства, своды правил и стандарты,
    /// затем остальное по алфавиту (авторы, затем название).
    /// </summary>
    public static IReadOnlyList<LiteratureEntry> Order(IEnumerable<LiteratureEntry> entries) =>
        entries
            .OrderBy(e => e.Title.StartsWith("Федеральный закон", StringComparison.Ordinal) ? 0
                : e.Title.StartsWith("Постановление", StringComparison.Ordinal) ? 1
                : NormTitle().IsMatch(e.Title) ? 2
                : 3)
            .ThenBy(e => NormTitle().IsMatch(e.Title) ? CodeKey(e.Title) : "", StringComparer.Ordinal)
            .ThenBy(e => e.Authors is "" or "—" ? e.Title : e.Authors, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), false))
            .ToList();

    // Нормы по обозначению: вид документа, затем номер по возрастанию (СП 7 раньше СП 30, СП 30 раньше СП 485).
    private static string CodeKey(string title)
    {
        var code = NormTitle().Match(title).Groups["code"].Value;
        var kind = code.StartsWith("ГОСТ", StringComparison.Ordinal) ? "1" : code.StartsWith("СП", StringComparison.Ordinal) ? "2" : "3";
        return kind + Digits().Replace(code, m => m.Value.PadLeft(8, '0'));
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    private static string Year(string year) => year.Length > 0 ? year : "[год]";

    // «СП 485.1311500.2020 «Название» остаток» — обозначение, название в кавычках-ёлочках и редакция.
    [GeneratedRegex(@"^(?<code>(СП|ГОСТ( Р)?|СН КР|СНиП|НПБ|ВСН)\s[^«]+?)\s*«(?<name>[^»]+)»\s*(?<rest>.*)$")]
    private static partial Regex NormTitle();
}
