using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// Выгрузка docx выпуска в PDF через установленный Microsoft Word (COM, только Windows): перед сохранением обновляются
/// оглавление и поля — в PDF для экспертизы содержание заполнено. Word не установлен — сообщение, без исключения.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WordPdf
{
    private const int PdfFormat = 17; // wdExportFormatPDF

    public static int Export(string folder)
    {
        var files = Directory.GetFiles(folder, "*.docx", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal)).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"В папке {folder} нет файлов docx.");
            return 2;
        }

        if (Type.GetTypeFromProgID("Word.Application") is not { } type)
        {
            Console.Error.WriteLine("Microsoft Word не установлен: откройте docx в Word или LibreOffice, обновите поля (F9) и сохраните в PDF вручную.");
            return 2;
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
                    Console.WriteLine($"  {pdf}");
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

        return 0;
    }
}
