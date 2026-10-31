using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>
/// Перечень сигналов АУПТ в АПС (ADR 0012, этап 1) в xlsx: одна строка на точку управления — обозначение устройства,
/// номер позиции спецификации (как на схеме насосной), секция, оборудование, вид сигнала и пункт нормы. Рабочая таблица для ТЗ на взаимодействие и таблицы адресов; приборы и шлейфы назначает проектировщик АПС.
/// </summary>
public static class SignalListXlsx
{
    public const int HeaderRow = 3;

    public static readonly IReadOnlyList<string> Columns = ["№ п/п", "Обозначение", "Поз.", "Секция", "Оборудование", "Сигнал", "Основание"];

    private static readonly double[] Widths = [7, 14, 7, 11, 56, 36, 34];

    public static string KindText(SignalKind kind) => kind switch
    {
        SignalKind.ValvePosition => "Положение запорного устройства",
        SignalKind.PressureSwitch => "Срабатывание сигнализатора давления",
        SignalKind.PumpStart => "Пуск (обобщённый выход)",
        SignalKind.PumpFault => "Неисправность (обобщённый выход)",
        SignalKind.AutomaticsOff => "Автоматика отключена",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <param name="positions">Номера позиций спецификации по роли; без номера ячейка «Поз.» пустая.</param>
    public static void Write(string path, IReadOnlyList<ControlPoint> points, string title, string font = DocumentStyle.DefaultFont, IReadOnlyDictionary<string, int>? positions = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        using var book = new XLWorkbook();
        var ws = book.AddWorksheet("Сигналы");
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
        for (var i = 0; i < points.Count; i++, row++)
        {
            var p = points[i];
            ws.Cell(row, 1).Value = i + 1;
            ws.Cell(row, 2).Value = p.Designation;
            if (p.Role is not null && positions?.TryGetValue(p.Role, out var pos) == true)
            {
                ws.Cell(row, 3).Value = pos;
            }

            ws.Cell(row, 4).Value = p.Section is { } section ? section : "насосная";
            ws.Cell(row, 5).Value = p.Equipment;
            ws.Cell(row, 6).Value = KindText(p.Kind);
            ws.Cell(row, 7).Value = p.Clause ?? "не указано";
        }

        var table = ws.Range(HeaderRow, 1, Math.Max(HeaderRow, row - 1), Columns.Count);
        table.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        table.Style.Alignment.SetWrapText();
        book.SaveAs(path);
    }
}
