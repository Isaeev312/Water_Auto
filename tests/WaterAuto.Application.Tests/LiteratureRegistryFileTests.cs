using WaterAuto.Reports;

namespace WaterAuto.Application.Tests;

public class LiteratureRegistryFileTests
{
    [Fact]
    public void Registry_has_no_repeated_documents_and_no_shifted_columns()
    {
        // Повтор строки дал бы документ дважды в списке источников записки; поле с запятыми без кавычек сдвигает колонки.
        using var reader = new StreamReader(RepoPaths.Registry);
        var entries = LiteratureRegistry.Parse(reader);
        entries.GroupBy(e => e.Id).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty();
        entries.GroupBy(e => e.Title).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty();
        // Год — четыре цифры в начале («1991 (изд. 2015)») или прочерк; сдвиг колонок ставит сюда чужой текст.
        var bad = entries.Where(e => e.Year.Length > 0 && e.Year != "—" && !(e.Year.Length >= 4 && e.Year[..4].All(char.IsDigit))).Select(e => e.Id).ToList();
        bad.ShouldBeEmpty();
    }
}
