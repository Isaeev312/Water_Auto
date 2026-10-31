using System.Globalization;
using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports;

/// <summary>
/// Расчёт ёмкости АКБ приборов автоматики АУПТ в АПС (прил. Б СП 6.13130.2025) в xlsx: приборы с токами и источниками,
/// затем суммарные токи, коэффициенты, требуемая ёмкость и время работы выбранной АКБ.
/// </summary>
public static class PowerBudgetXlsx
{
    public const int HeaderRow = 3;

    public static readonly IReadOnlyList<string> Columns = ["№ п/п", "Прибор", "Кол.", "Iд, мА", "Iп, мА", "Примечание", "Источник данных"];

    private static readonly double[] Widths = [7, 30, 7, 10, 10, 40, 60];

    public static void Write(string path, PowerBudget budget, string title, string font = DocumentStyle.DefaultFont)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        using var book = new XLWorkbook();
        var ws = book.AddWorksheet("АКБ");
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
        for (var i = 0; i < budget.Lines.Count; i++, row++)
        {
            var l = budget.Lines[i];
            ws.Cell(row, 1).Value = i + 1;
            ws.Cell(row, 2).Value = l.Name;
            ws.Cell(row, 3).Value = l.Count;
            ws.Cell(row, 4).Value = Milli(l.Standby, ru);
            ws.Cell(row, 5).Value = Milli(l.Alarm, ru);
            ws.Cell(row, 6).Value = l.Note;
            ws.Cell(row, 7).Value = l.Source;
        }

        var table = ws.Range(HeaderRow, 1, row - 1, Columns.Count);
        table.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        table.Style.Alignment.SetWrapText();

        row++;
        var b = budget.Battery;
        (string Name, string Value)[] summary =
        [
            ("Напряжение питания, В", budget.Voltage.Volts.ToString("0.#", ru)),
            ("ΣIд — ток в дежурном режиме, мА", Milli(budget.Standby, ru)),
            ("ΣIп — ток в режиме «Пожар», мА", Milli(budget.Alarm, ru)),
            ("Iибэ — собственный ток РИП от АКБ, мА", Milli(budget.SupplyOwn, ru)),
            ("Число источников (по току «Пожар» против паспортного РИП)", budget.Supplies.ToString(ru)),
            ("Iд, Iп на один источник (нагрузка поровну), мА", $"{Milli(budget.StandbyPerSupply, ru)}; {Milli(budget.AlarmPerSupply, ru)}"),
            ("АКБ (на каждый источник)", $"{b.Name}, C20 = {b.Nominal.AmpereHours.ToString("0.##", ru)} А·ч; {b.Source}"),
            ("Kстр = 100 % / S (Б.2)", budget.AgingFactor?.ToString("0.###", ru) ?? "не определён"),
            ("Kн дежурного режима (паспорт АКБ)", budget.StandbyLoadFactor?.ToString("0.###", ru) ?? "не определён"),
            ("Kн режима «Пожар» (паспорт АКБ)", budget.AlarmLoadFactor?.ToString("0.###", ru) ?? "не определён"),
            ("Требуемая ёмкость АКБ, А·ч", budget.Required?.AmpereHours.ToString("0.##", ru) ?? "не определена"),
            ("Время работы выбранной АКБ в режиме «Пожар» (Б.3), ч", budget.Backup?.TotalHours.ToString("0.0", ru) ?? "не определено"),
        ];
        var first = row;
        foreach (var (name, value) in summary)
        {
            ws.Cell(row, 2).Value = name;
            ws.Range(row, 2, row, 5).Merge();
            ws.Cell(row, 6).Value = value;
            ws.Range(row, 6, row, 7).Merge();
            row++;
        }

        var block = ws.Range(first, 2, row - 1, Columns.Count);
        block.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        block.Style.Alignment.SetWrapText();
        ws.Cell(row + 1, 2).Value = "Расчёт — по прил. Б СП 6.13130.2025, формула Б.1.1 с током режима «Пожар» во втором слагаемом (см. замечания расчёта).";
        book.SaveAs(path);
    }

    private static string Milli(Current? current, CultureInfo ru) => current is { } c ? (c.Amperes * 1000).ToString("0.#", ru) : "—";
}
