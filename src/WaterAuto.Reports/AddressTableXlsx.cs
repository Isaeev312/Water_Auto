using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>Таблица адресов (ADR 0012, этап 1) в xlsx: контроллер, модуль, вход, обозначение, оборудование, сигнал.</summary>
public static class AddressTableXlsx
{
    public const int HeaderRow = 3;

    public static readonly IReadOnlyList<string> Columns = ["№ п/п", "Контроллер", "Модуль", "Вход", "Обозначение", "Оборудование", "Сигнал"];

    private static readonly double[] Widths = [7, 18, 18, 7, 14, 56, 36];

    public static void Write(string path, IReadOnlyList<AddressRow> rows, string title, string font = DocumentStyle.DefaultFont)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var book = new XLWorkbook();
        var ws = book.AddWorksheet("Адреса");
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
            ws.Cell(row, 2).Value = r.Controller;
            ws.Cell(row, 3).Value = r.Module;
            ws.Cell(row, 4).Value = r.Input;
            ws.Cell(row, 5).Value = r.Designation;
            ws.Cell(row, 6).Value = r.Equipment;
            ws.Cell(row, 7).Value = r.Signal;
        }

        var table = ws.Range(HeaderRow, 1, Math.Max(HeaderRow, row - 1), Columns.Count);
        table.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        table.Style.Alignment.SetWrapText();
        ws.Cell(row + 1, 2).Value = "Адреса устройств на линии контроллера и помещения приборов назначает проектировщик АПС.";
        book.SaveAs(path);
    }
}
