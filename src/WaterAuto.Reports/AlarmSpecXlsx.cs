using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>Спецификация оборудования АПС (ADR 0012) в xlsx: позиция, наименование, марка, изготовитель, количество, сертификат.</summary>
public static class AlarmSpecXlsx
{
    public const int HeaderRow = 3;

    public static readonly IReadOnlyList<string> Columns = ["Поз.", "Наименование", "Тип, марка", "Изготовитель", "Ед.", "Кол.", "Сертификат", "Примечание"];

    private static readonly double[] Widths = [6, 42, 26, 18, 6, 7, 48, 40];

    public static void Write(string path, IReadOnlyList<AlarmSpecItem> items, string title, string font = DocumentStyle.DefaultFont)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var book = new XLWorkbook();
        var ws = book.AddWorksheet("АПС.СО");
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
        for (var i = 0; i < items.Count; i++, row++)
        {
            var it = items[i];
            ws.Cell(row, 1).Value = i + 1;
            ws.Cell(row, 2).Value = it.Name;
            ws.Cell(row, 3).Value = it.Designation;
            ws.Cell(row, 4).Value = it.Manufacturer;
            ws.Cell(row, 5).Value = it.Unit;
            ws.Cell(row, 6).Value = it.Quantity;
            ws.Cell(row, 7).Value = string.IsNullOrWhiteSpace(it.Certificate) ? "не указан" : it.Certificate;
            ws.Cell(row, 8).Value = it.Note;
        }

        var table = ws.Range(HeaderRow, 1, Math.Max(HeaderRow, row - 1), Columns.Count);
        table.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        table.Style.Alignment.SetWrapText();
        book.SaveAs(path);
    }
}
