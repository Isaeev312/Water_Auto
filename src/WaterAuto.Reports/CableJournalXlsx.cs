using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>Кабельный журнал АПС (ГОСТ 21.608 по составу граф, упрощённо) в xlsx.</summary>
public static class CableJournalXlsx
{
    public const int HeaderRow = 3;

    public static readonly IReadOnlyList<string> Columns = ["№", "Обозначение", "Откуда", "Куда", "Назначение", "Марка, сечение", "Длина, м", "Примечание"];

    private static readonly double[] Widths = [5, 12, 26, 34, 28, 28, 10, 56];

    public static void Write(string path, IReadOnlyList<CableLine> lines, string title, string font = DocumentStyle.DefaultFont)
    {
        ArgumentNullException.ThrowIfNull(lines);
        using var book = new XLWorkbook();
        var ws = book.AddWorksheet("КЖ");
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
        for (var i = 0; i < lines.Count; i++, row++)
        {
            var l = lines[i];
            ws.Cell(row, 1).Value = i + 1;
            ws.Cell(row, 2).Value = l.Id;
            ws.Cell(row, 3).Value = l.From;
            ws.Cell(row, 4).Value = l.To;
            ws.Cell(row, 5).Value = l.Purpose;
            ws.Cell(row, 6).Value = l.Cable;
            if (l.LengthM > 0) { ws.Cell(row, 7).Value = l.LengthM; } else { ws.Cell(row, 7).Value = "—"; }
            ws.Cell(row, 8).Value = l.Note;
        }

        ws.Cell(row, 6).Value = "Итого, м:";
        ws.Cell(row, 7).Value = lines.Sum(l => l.LengthM);
        var table = ws.Range(HeaderRow, 1, row, Columns.Count);
        table.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        table.Style.Alignment.SetWrapText();
        ws.Cell(row + 2, 2).Value = "Кабели — с медными жилами, не распространяющие горение, огнестойкие по области применения прил. Г СП 6.13130.2025 (п. 6.2); время работоспособности — п. 6.17, табл. 6.2. Длины — по плану с запасом, уточняются по трассам.";
        book.SaveAs(path);
    }
}
