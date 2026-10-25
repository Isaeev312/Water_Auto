using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>Заголовок листа спецификации (объект, марка документа).</summary>
public sealed record SpecificationHeader(string Title);

/// <summary>
/// Спецификация оборудования, изделий и материалов по форме 1 ГОСТ 21.110 в xlsx (лист A3, альбомный).
/// Разделы — оросители, трубопроводы, фасонные части; позиции нумеруются подряд. Рамку и основную надпись
/// добавляет PDF-выпуск или проектировщик — в xlsx их нет.
/// </summary>
public static class SpecificationXlsx
{
    public const int HeaderRow = 3;

    /// <summary>Графы формы 1 ГОСТ 21.110.</summary>
    public static readonly IReadOnlyList<string> Columns =
    [
        "Позиция",
        "Наименование и техническая характеристика",
        "Тип, марка оборудования. Обозначение документа, опросного листа",
        "Код продукции",
        "Поставщик",
        "Единица измерения",
        "Количество",
        "Масса 1 ед., кг",
        "Примечание",
    ];

    // Ширины граф формы 1, мм; в Excel ширина — в символах (≈ 1,9 мм на символ шрифта по умолчанию).
    private static readonly double[] WidthsMm = [20, 130, 60, 35, 45, 20, 20, 25, 40];

    private static readonly (TakeoffGroup Group, string Title)[] Sections =
    [
        (TakeoffGroup.Sprinklers, "Оросители"),
        (TakeoffGroup.Pipes, "Трубопроводы"),
        (TakeoffGroup.Fittings, "Фасонные части"),
        (TakeoffGroup.Hydrants, "Пожарные краны"),
        (TakeoffGroup.Supports, "Крепления трубопроводов"),
    ];

    public static void Write(string path, MaterialTakeoff takeoff, SpecificationHeader header)
    {
        ArgumentNullException.ThrowIfNull(takeoff);
        ArgumentNullException.ThrowIfNull(header);
        using var book = new XLWorkbook();
        var ws = book.AddWorksheet("Спецификация");

        ws.Cell(1, 1).Value = header.Title;
        ws.Range(1, 1, 1, Columns.Count).Merge().Style.Font.SetBold().Font.SetFontSize(14);
        for (var c = 0; c < Columns.Count; c++)
        {
            ws.Column(c + 1).Width = WidthsMm[c] / 1.9;
            ws.Cell(HeaderRow, c + 1).Value = Columns[c];
            ws.Cell(HeaderRow + 1, c + 1).Value = c + 1;
        }

        var head = ws.Range(HeaderRow, 1, HeaderRow + 1, Columns.Count);
        head.Style.Alignment.SetWrapText().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Alignment.SetVertical(XLAlignmentVerticalValues.Center);

        var row = HeaderRow + 2;
        var position = 0;
        foreach (var (group, title) in Sections)
        {
            var items = takeoff.Items.Where(i => i.Group == group).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            ws.Cell(row, 2).Value = title;
            ws.Cell(row, 2).Style.Font.SetBold().Font.SetUnderline();
            row++;
            foreach (var item in items)
            {
                ws.Cell(row, 1).Value = (++position).ToString(System.Globalization.CultureInfo.InvariantCulture);
                ws.Cell(row, 2).Value = item.Name;
                ws.Cell(row, 3).Value = item.Designation ?? "";
                ws.Cell(row, 6).Value = item.Unit;
                ws.Cell(row, 7).Value = item.Quantity;
                ws.Cell(row, 7).Style.NumberFormat.Format = "0.##";
                ws.Cell(row, 9).Value = item.Note ?? "";
                row++;
            }
        }

        var table = ws.Range(HeaderRow, 1, Math.Max(row - 1, HeaderRow + 1), Columns.Count);
        table.Style.Border.SetInsideBorder(XLBorderStyleValues.Thin).Border.SetOutsideBorder(XLBorderStyleValues.Thin);
        table.Style.Alignment.SetWrapText();
        ws.Range(HeaderRow + 2, 1, Math.Max(row - 1, HeaderRow + 2), 1).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        ws.PageSetup.PaperSize = XLPaperSize.A3Paper;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.SetRowsToRepeatAtTop(HeaderRow, HeaderRow + 1);
        ws.PageSetup.FitToPages(1, 0);
        book.SaveAs(path);
    }
}
