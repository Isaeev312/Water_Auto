using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace WaterAuto.Reports;

/// <summary>
/// Простой читатель docx для предпросмотра в приложении: абзацы основного текста (включая таблицы — они тоже
/// состоят из абзацев). Берутся только видимые текстовые прогоны (w:t), поэтому служебные коды полей (например,
/// оглавления) в предпросмотр не попадают; пустые абзацы сохраняются как пустые строки — по ним видны интервалы
/// записки. Без типов OpenXml в сигнатурах; документ открывается только для чтения и закрывается сразу.
/// Ограничение — первые <see cref="MaxParagraphs"/> абзацев.
/// </summary>
public static class DocxPreview
{
    /// <summary>Сколько первых абзацев показывать (защита от гигантских документов).</summary>
    public const int MaxParagraphs = 500;

    /// <summary>
    /// Абзацы основного текста документа. Пустые абзацы — пустые строки (интервалы сохраняются).
    /// Исключения: <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> — файл недоступен,
    /// <see cref="InvalidDataException"/> — файл не docx или в нём нет основного текста.
    /// </summary>
    public static IReadOnlyList<string> ReadParagraphs(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Файл docx не найден: {path}", path);
        }

        try
        {
            using var document = WordprocessingDocument.Open(path, false);
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new InvalidDataException($"В файле docx нет основного текста: {path}");
            return [.. body.Descendants<Paragraph>().Take(MaxParagraphs)
                .Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text)).Trim())];
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException and not InvalidDataException)
        {
            throw new InvalidDataException($"Не удалось прочитать docx «{Path.GetFileName(path)}»: {ex.Message}", ex);
        }
    }
}
