using System.Text.RegularExpressions;
using WaterAuto.Reports;

namespace WaterAuto.Application.Tests;

/// <summary>
/// Проверка текстов выпуска (docx и xlsx): нет следов незаполненной подстановки ({, }), нечисел (NaN, ∞, Infinity),
/// десятичной точки перед единицей измерения и английских уровней замечаний — тексты для экспертизы в русской записи. DXF не проверяется:
/// в MText фигурные скобки — разметка.
/// </summary>
internal static partial class OutputText
{
    public static void ShouldBeClean(IEnumerable<string> files)
    {
        var problems = new List<string>();
        foreach (var file in files)
        {
            var lines = Path.GetExtension(file) switch
            {
                ".docx" => DocxPreview.ReadParagraphs(file),
                ".xlsx" => XlsxPreview.ReadFirstSheet(file).Select(r => string.Join(" | ", r)).ToList(),
                _ => [],
            };
            foreach (var line in lines)
            {
                if (line.Contains('{', StringComparison.Ordinal) || line.Contains('}', StringComparison.Ordinal)
                    || line.Contains("NaN", StringComparison.Ordinal) || line.Contains('∞', StringComparison.Ordinal)
                    || line.Contains("Infinity", StringComparison.Ordinal) || DotDecimal().IsMatch(line)
                    || line.Contains("Warning", StringComparison.Ordinal) || line.Contains("Error:", StringComparison.Ordinal))
                {
                    problems.Add($"{Path.GetFileName(file)}: {line}");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems.Take(20)));
    }

    [GeneratedRegex(@"\d\.\d+ ?(м|мм|МПа|л/с|м/с|м²|м³)(?![\p{L}])")]
    private static partial Regex DotDecimal();
}
