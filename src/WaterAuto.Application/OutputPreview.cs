using WaterAuto.Reports;

namespace WaterAuto.Application;

/// <summary>Таблица для предпросмотра xlsx: строки и ячейки как отформатированные строки.</summary>
public sealed record PreviewTable(IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Текст для предпросмотра docx: абзацы как строки (пустые — интервалы).</summary>
public sealed record PreviewText(IReadOnlyList<string> Lines);

/// <summary>
/// Предпросмотр выпущенных документов без Excel/Word: определяет поддерживаемый формат по расширению и читает
/// файл простыми читателями <see cref="XlsxPreview"/> (первый лист книги) и <see cref="DocxPreview"/> (абзацы).
/// Форматы и ограничения — в читателях; ошибки чтения — IOException/UnauthorizedAccessException/InvalidDataException,
/// окно предпросмотра показывает их сообщением, а не падает.
/// </summary>
public static class OutputPreview
{
    /// <summary>Поддерживается ли предпросмотр файла (только .xlsx и .docx, регистр расширения не важен).</summary>
    public static bool CanPreview(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".docx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Прочитать файл для предпросмотра: <see cref="PreviewTable"/> для xlsx, <see cref="PreviewText"/> для docx;
    /// для остальных расширений — <see cref="InvalidDataException"/>.
    /// </summary>
    public static object Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return new PreviewTable(XlsxPreview.ReadFirstSheet(path));
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return new PreviewText(DocxPreview.ReadParagraphs(path));
        }

        throw new InvalidDataException($"Предпросмотр поддерживает только .xlsx и .docx: {path}");
    }
}
