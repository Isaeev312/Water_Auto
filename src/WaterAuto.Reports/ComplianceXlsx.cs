using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>Таблица соответствия требованиям норм в xlsx: требование, пункт, требуется, принято, вывод — для экспертизы.</summary>
public static class ComplianceXlsx
{
    public const int HeaderRow = 3;

    public static readonly IReadOnlyList<string> Columns = ["№ п/п", "Требование", "Документ, пункт", "Требуется", "Принято в проекте", "Вывод"];

    private static readonly double[] Widths = [7, 44, 30, 30, 36, 18];

    public static string VerdictText(ComplianceVerdict verdict) => verdict switch
    {
        ComplianceVerdict.Complies => "соответствует",
        ComplianceVerdict.Violates => "не соответствует",
        _ => "проверить",
    };

    public static void Write(string path, IReadOnlyList<ComplianceRow> rows, string title, string font = DocumentStyle.DefaultFont)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var book = new XLWorkbook();
        var ws = book.AddWorksheet("Соответствие");
        ws.Style.Font.FontName = font;
        ws.Style.Font.FontSize = 11;

        ws.Cell(1, 1).Value = title;
        ws.Range(1, 1, 1, Columns.Count).Merge().Style.Font.SetBold().Font.SetFontSize(14);
        for (var c = 0; c < Columns.Count; c++)
        {
            ws.Column(c + 1).Width = Widths[c];
            ws.Cell(HeaderRow, c + 1).Value = Columns[c];
        }

        ws.Range(HeaderRow, 1, HeaderRow, Columns.Count).Style.Font.SetBold().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        var row = HeaderRow + 1;
        for (var i = 0; i < rows.Count; i++, row++)
        {
            var r = rows[i];
            ws.Cell(row, 1).Value = i + 1;
            ws.Cell(row, 2).Value = r.Requirement;
            ws.Cell(row, 3).Value = r.Clause;
            ws.Cell(row, 4).Value = r.Required;
            ws.Cell(row, 5).Value = r.Adopted;
            ws.Cell(row, 6).Value = VerdictText(r.Verdict);
            ws.Cell(row, 6).Style.Font.FontColor = r.Verdict switch
            {
                ComplianceVerdict.Complies => XLColor.DarkGreen,
                ComplianceVerdict.Violates => XLColor.DarkRed,
                _ => XLColor.DarkOrange,
            };
        }

        var table = ws.Range(HeaderRow, 1, Math.Max(HeaderRow, row - 1), Columns.Count);
        table.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        table.Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
        ws.Cell(row + 1, 2).Value = "Значения — по расчёту программы и данным норм; строки «проверить» требуют решения проектировщика или подтверждения по ТД изделий.";
        book.SaveAs(path);
    }
}
