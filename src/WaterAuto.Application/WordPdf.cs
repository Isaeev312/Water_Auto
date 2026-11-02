using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WaterAuto.Application;

/// <summary>
/// Выгрузка docx выпуска в PDF через установленный Microsoft Word (COM, только Windows): перед сохранением обновляются
/// оглавление и поля — в PDF для экспертизы содержание заполнено. Word не установлен — сообщение, без исключения.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WordPdf
{
    private const int PdfFormat = 17; // wdExportFormatPDF

    /// <summary>Выгружает все docx папки (с подпапками) в PDF рядом с ними. Сообщения — в <paramref name="log"/>; результат — список PDF или null, если выгрузить нельзя.</summary>
    public static IReadOnlyList<string>? Export(string folder, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var done = new List<string>();
        var files = Directory.GetFiles(folder, "*.docx", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal)).ToList();
        if (files.Count == 0)
        {
            log($"В папке {folder} нет файлов docx.");
            return null;
        }

        if (Type.GetTypeFromProgID("Word.Application") is not { } type)
        {
            log("Microsoft Word не установлен: откройте docx в Word или LibreOffice, обновите поля (F9) и сохраните в PDF вручную.");
            return null;
        }

        dynamic word = Activator.CreateInstance(type)!;
        try
        {
            word.Visible = false;
            word.DisplayAlerts = 0;
            foreach (var file in files)
            {
                dynamic doc = word.Documents.Open(file, false, false, false);
                try
                {
                    foreach (dynamic toc in doc.TablesOfContents)
                    {
                        toc.Update();
                    }

                    doc.Fields.Update();
                    var pdf = Path.ChangeExtension(file, ".pdf");
                    doc.ExportAsFixedFormat(pdf, PdfFormat);
                    done.Add(pdf);
                    log(pdf);
                }
                finally
                {
                    doc.Close(false);
                    Marshal.FinalReleaseComObject(doc);
                }
            }
        }
        finally
        {
            word.Quit();
            Marshal.FinalReleaseComObject(word);
        }

        return done;
    }
}
