using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>Заголовок листа спецификации (объект, марка документа).</summary>
public sealed record SpecificationHeader(string Title);

/// <summary>
/// Спецификация оборудования, изделий и материалов по форме 1 ГОСТ 21.110-2013 в xlsx (A3, альбомная).
/// Графы и ширины — по форме 1; заголовки разделов — в графе 2, подчёркнуты; позиции нумеруются подряд;
/// обозначения документов — с годом утверждения; масса единицы, кг, где она известна (трубы — теоретическая масса 1 м);
/// в конце — итог массы позиций с известной массой. Шрифт — GOST Common (ГОСТ 2.304, тип А).
/// Рамку и основную надпись (ГОСТ Р 21.101, форма 3) проектировщик добавляет при выпуске листа.
/// </summary>
public static class SpecificationXlsx
{
    public const int HeaderRow = 3;

    /// <summary>Шрифт листа (ГОСТ 2.304, тип А); если его нет на компьютере, Excel подставит замену.</summary>
    public const string Font = "GOST Common";

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
        "Масса единицы, кг",
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
        ws.Style.Font.FontName = Font;
        ws.Style.Font.FontSize = 11;
        ws.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Cell(1, 1).Value = header.Title;
        ws.Range(1, 1, 1, Columns.Count).Merge().Style.Font.SetBold().Font.SetFontSize(14).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        for (var c = 0; c < Columns.Count; c++)
        {
            ws.Column(c + 1).Width = WidthsMm[c] / 1.9;
            ws.Cell(HeaderRow, c + 1).Value = Columns[c];
            ws.Cell(HeaderRow + 1, c + 1).Value = c + 1;
        }

        var head = ws.Range(HeaderRow, 1, HeaderRow + 1, Columns.Count);
        head.Style.Alignment.SetWrapText().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Alignment.SetVertical(XLAlignmentVerticalValues.Center);
        ws.Row(HeaderRow).Height = 45;

        var row = HeaderRow + 2;
        var position = 0;
        var totalMass = 0.0;
        foreach (var (group, title) in Sections)
        {
            var items = takeoff.Items.Where(i => i.Group == group).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            ws.Cell(row, 2).Value = title;
            ws.Cell(row, 2).Style.Font.SetUnderline().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            row++;
            foreach (var item in items)
            {
                ws.Cell(row, 1).Value = (++position).ToString(System.Globalization.CultureInfo.InvariantCulture);
                ws.Cell(row, 2).Value = item.Name;
                ws.Cell(row, 3).Value = item.Designation ?? "";
                ws.Cell(row, 5).Value = item.Supplier ?? "";
                ws.Cell(row, 6).Value = item.Unit;
                ws.Cell(row, 7).Value = item.Quantity;
                ws.Cell(row, 7).Style.NumberFormat.Format = Math.Abs(item.Quantity - Math.Round(item.Quantity)) < 1e-9 ? "0" : "0.00";
                if (item.UnitMass is { } mass)
                {
                    ws.Cell(row, 8).Value = mass;
                    ws.Cell(row, 8).Style.NumberFormat.Format = "0.00";
                    totalMass += mass * item.Quantity;
                }

                ws.Cell(row, 9).Value = item.Note ?? "";
                row++;
            }
        }

        if (totalMass > 0)
        {
            ws.Cell(row, 2).Value = "Итого масса позиций с указанной массой, кг";
            ws.Cell(row, 8).Value = Math.Round(totalMass, 1);
            ws.Cell(row, 8).Style.NumberFormat.Format = "0.0";
            ws.Row(row).Style.Font.SetBold();
            row++;
        }

        var table = ws.Range(HeaderRow, 1, Math.Max(row - 1, HeaderRow + 1), Columns.Count);
        table.Style.Border.SetInsideBorder(XLBorderStyleValues.Thin).Border.SetOutsideBorder(XLBorderStyleValues.Thin);
        table.Style.Alignment.SetWrapText();
        foreach (var c in new[] { 1, 6, 7, 8 })
        {
            ws.Range(HeaderRow + 2, c, Math.Max(row - 1, HeaderRow + 2), c).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        }

        ws.PageSetup.PaperSize = XLPaperSize.A3Paper;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.SetRowsToRepeatAtTop(HeaderRow, HeaderRow + 1);
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.Left = 20 / 25.4;
        ws.PageSetup.Margins.Right = 5 / 25.4;
        ws.PageSetup.Margins.Top = 5 / 25.4;
        ws.PageSetup.Margins.Bottom = 5 / 25.4;
        ws.PageSetup.Footer.Right.AddText("Лист &P");
        book.SaveAs(path);
    }
}
