using ClosedXML.Excel;

namespace WaterAuto.Reports;

/// <summary>
/// Простой читатель xlsx для предпросмотра в приложении: первый лист книги, значения ячеек как отформатированные
/// строки (<c>GetFormattedString</c>), без типов ClosedXML в сигнатурах. Книга читается только на просмотр и
/// закрывается сразу; объединённые ячейки повторяют значение только в первой ячейке (допустимо для предпросмотра).
/// Ограничения — не больше <see cref="MaxRows"/> строк и <see cref="MaxColumns"/> колонок, пустые строки в конце
/// отбрасываются: окно предпросмотра не должно зависать на очень больших файлах.
/// </summary>
public static class XlsxPreview
{
    /// <summary>Сколько первых строк листа показывать (защита от гигантских книг).</summary>
    public const int MaxRows = 300;

    /// <summary>Сколько первых колонок листа показывать.</summary>
    public const int MaxColumns = 30;

    /// <summary>
    /// Строки и ячейки первого листа книги. Пустые строки внутри сохраняются (нужны интервалы формы 1),
    /// полностью пустые строки в конце — отбрасываются.
    /// Исключения: <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> — файл недоступен,
    /// <see cref="InvalidDataException"/> — файл не xlsx или в книге нет листов.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadFirstSheet(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Файл xlsx не найден: {path}", path);
        }

        using var stream = File.OpenRead(path);
        using var workbook = OpenWorkbook(stream, path);
        var sheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidDataException($"В файле xlsx нет листов: {path}");

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        var rowCount = Math.Min(lastRow, MaxRows);
        var columnCount = Math.Min(lastColumn, MaxColumns);
        var rows = new List<IReadOnlyList<string>>(rowCount);
        for (var rowNumber = 1; rowNumber <= rowCount; rowNumber++)
        {
            var cells = new string[columnCount];
            var empty = true;
            for (var columnNumber = 1; columnNumber <= columnCount; columnNumber++)
            {
                var value = Format(sheet.Cell(rowNumber, columnNumber));
                cells[columnNumber - 1] = value;
                if (value.Length > 0)
                {
                    empty = false;
                }
            }

            if (!empty)
            {
                rows.Add(cells);
            }
            else if (rows.Count > 0)
            {
                rows.Add(cells); // пустая строка внутри — сохраняем, в конце отбросим
            }
        }

        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty))
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return rows;
    }

    private static XLWorkbook OpenWorkbook(Stream stream, string path)
    {
        try
        {
            return new XLWorkbook(stream);
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Не удалось прочитать xlsx «{Path.GetFileName(path)}»: {ex.Message}", ex);
        }
    }

    // Значение ячейки как в Excel: формат числа/даты из книги, обрезка, переводы строк — в пробелы.
    private static string Format(IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return "";
        }

        var text = cell.GetFormattedString().Trim();
        return text.Contains('\n') || text.Contains('\r')
            ? text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ')
            : text;
    }
}
