using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>Заголовок листа спецификации (объект, марка документа).</summary>
public sealed record SpecificationHeader(string Title);

/// <summary>
/// Спецификация оборудования, изделий и материалов по форме 1 ГОСТ 21.110-2013 в xlsx (A3, альбомная).
/// Графы и ширины — по форме 1; заголовки разделов — в графе 2, подчёркнуты;
/// обозначения документов — с годом утверждения; масса единицы, кг, где она известна (трубы — теоретическая масса 1 м);
/// в конце — итог массы позиций с известной массой. Шрифт — шрифт проекта (по умолчанию GOST Common).
/// Позиции — в графе «Позиция», заново в каждом разделе (как в эталоне ИОС2.2, решение пользователя); заголовок раздела отделён от позиций пустой строкой (п. 4.5); строки — по 8 мм (длинный текст — кратно 8 мм).
/// Рабочий файл для правки; для выпуска — <see cref="SpecificationDocx"/> в рамке с основной надписью. Внизу оставлено место под основную надпись.
/// </summary>
public static class SpecificationXlsx
{
    public const int HeaderRow = 3;

    /// <summary>Высота строки формы 1 — 8 мм, пт; длинный текст занимает кратное число строк.</summary>
    public const double RowHeightPt = 8 / 25.4 * 72;

    /// <summary>Нижнее поле под основную надпись (ГОСТ Р 21.101, форма 3 — 55 мм) и рамку, мм.</summary>
    public const double TitleBlockMm = 60;

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
    internal static readonly double[] WidthsMm = [20, 130, 60, 35, 45, 20, 20, 25, 40];

    internal static readonly (TakeoffGroup Group, string Title)[] Sections =
    [
        (TakeoffGroup.PumpStation, "Оборудование"),
        (TakeoffGroup.PumpStationValves, "Арматура и приборы"),
        (TakeoffGroup.PumpStationPipes, "Трубопроводы обвязки"),
        (TakeoffGroup.PumpStationFittings, "Фасонные части обвязки"),
        (TakeoffGroup.ControlUnit, "Узел управления"),
        (TakeoffGroup.Sprinklers, "Оросители"),
        (TakeoffGroup.Pipes, "Трубопроводы"),
        (TakeoffGroup.Fittings, "Фасонные части"),
        (TakeoffGroup.Valves, "Промывочные устройства"),
        (TakeoffGroup.Hydrants, "Пожарные краны"),
        (TakeoffGroup.Supports, "Крепления трубопроводов"),
        (TakeoffGroup.Materials, "Материалы"),
    ];

    /// <summary>
    /// Позиции строк в порядке разделов: заново в каждом разделе или сквозные (<paramref name="continuous"/> — когда номера
    /// позиций стоят на схеме и не должны повторяться).
    /// </summary>
    public static IReadOnlyList<(TakeoffItem Item, int Position)> Positions(MaterialTakeoff takeoff, bool continuous = false)
    {
        ArgumentNullException.ThrowIfNull(takeoff);
        var list = new List<(TakeoffItem, int)>();
        var position = 0;
        foreach (var (group, _) in Sections)
        {
            position = continuous ? position : 0;
            list.AddRange(takeoff.Items.Where(i => i.Group == group).Select(i => (i, ++position)));
        }

        return list;
    }

    public static void Write(string path, MaterialTakeoff takeoff, SpecificationHeader header, string font = DocumentStyle.DefaultFont, bool continuous = false)
    {
        ArgumentNullException.ThrowIfNull(takeoff);
        ArgumentNullException.ThrowIfNull(header);
        using var book = new XLWorkbook();
        var ws = book.AddWorksheet("Спецификация");
        ws.Style.Font.FontName = font;
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
        var positions = Positions(takeoff, continuous);
        var totalMass = 0.0;

        // Высота строки: 8 мм на каждую строку текста самой длинной ячейки (оценка по ширине графы).
        void Height(int r, params string[] texts)
        {
            var lines = 1;
            for (var c = 0; c < texts.Length && c < WidthsMm.Length; c++)
            {
                var perLine = Math.Max(1, (int)(WidthsMm[c] / 1.8));
                lines = Math.Max(lines, (int)Math.Ceiling((double)Math.Max(1, texts[c].Length) / perLine));
            }

            ws.Row(r).Height = RowHeightPt * lines;
        }

        foreach (var (group, title) in Sections)
        {
            var items = takeoff.Items.Where(i => i.Group == group).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            ws.Cell(row, 2).Value = title;
            ws.Cell(row, 2).Style.Font.SetUnderline().Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(row).Height = RowHeightPt;

            // Заголовок отделён от позиций пустой строкой (ГОСТ 21.110, п. 4.5).
            ws.Row(row + 1).Height = RowHeightPt;
            row += 2;
            foreach (var item in items)
            {
                ws.Cell(row, 1).Value = positions.First(p => ReferenceEquals(p.Item, item)).Position.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
                Height(row, "", item.Name, item.Designation ?? "", "", item.Supplier ?? "", item.Unit, "", "", item.Note ?? "");
                row++;
            }
        }

        if (totalMass > 0)
        {
            ws.Cell(row, 2).Value = "Итого масса позиций с указанной массой, кг";
            ws.Cell(row, 8).Value = Math.Round(totalMass, 1);
            ws.Cell(row, 8).Style.NumberFormat.Format = "0.0";
            ws.Row(row).Style.Font.SetBold();
            ws.Row(row).Height = RowHeightPt;
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
        ws.PageSetup.Margins.Bottom = TitleBlockMm / 25.4;
        ws.PageSetup.Margins.Footer = 0;
        book.SaveAs(path);
    }
}
