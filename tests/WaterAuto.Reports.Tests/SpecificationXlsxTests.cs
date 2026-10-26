using ClosedXML.Excel;
using WaterAuto.Core;

namespace WaterAuto.Reports.Tests;

public class SpecificationXlsxTests
{
    private static readonly MaterialTakeoff Takeoff = new(
        [
            new(TakeoffGroup.Sprinklers, "Ороситель спринклерный водяной", "СВН-К115", 54, "шт."),
            new(TakeoffGroup.Sprinklers, "Ороситель спринклерный водяной (запас)", "СВН-К115", 5, "шт.", "запас по п. 6.1.15"),
            new(TakeoffGroup.Pipes, "Трубы стальные водогазопроводные DN25, Ø33,5×2,8 мм", "ГОСТ 3262", 119.12, "м"),
            new(TakeoffGroup.Fittings, "Отвод 90° DN100", null, 1, "шт.", "уточняется при оформлении узлов"),
        ],
        []);

    private static XLWorkbook WriteAndRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.xlsx");
        SpecificationXlsx.Write(path, Takeoff, new SpecificationHeader("Маликова 91. Автостоянка. АУПТ"));
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return new XLWorkbook(new MemoryStream(bytes));
    }

    [Fact]
    public void Columns_follow_GOST_21_110_form_1()
    {
        var ws = WriteAndRead().Worksheet(1);
        var headers = Enumerable.Range(1, 9).Select(c => ws.Cell(SpecificationXlsx.HeaderRow, c).GetString()).ToList();
        headers.ShouldBe(SpecificationXlsx.Columns);
        Enumerable.Range(1, 9).Select(c => ws.Cell(SpecificationXlsx.HeaderRow + 1, c).GetValue<int>()).ShouldBe(Enumerable.Range(1, 9));
        ws.Cell(1, 1).GetString().ShouldContain("Маликова 91");
    }

    [Fact]
    public void Items_are_grouped_and_numbered()
    {
        var ws = WriteAndRead().Worksheet(1);
        var rows = ws.RowsUsed().Where(r => r.RowNumber() > SpecificationXlsx.HeaderRow + 1).ToList();
        var names = rows.Select(r => r.Cell(2).GetString()).ToList();
        names.ShouldBe([
            "Оросители",
            "1 Ороситель спринклерный водяной",
            "2 Ороситель спринклерный водяной (запас)",
            "Трубопроводы",
            "1 Трубы стальные водогазопроводные DN25, Ø33,5×2,8 мм",
            "Фасонные части",
            "1 Отвод 90° DN100",
        ]);

        // ГОСТ 21.110, п. 4.7: порядковый номер — перед наименованием, заново в каждом разделе; графа «Позиция» — для
        // позиционных обозначений с чертежей, которых нет.
        rows.ShouldAllBe(r => r.Cell(1).GetString().Length == 0);
        var pipe = rows.Single(r => r.Cell(2).GetString().StartsWith("1 Трубы", StringComparison.Ordinal));
        pipe.Cell(3).GetString().ShouldBe("ГОСТ 3262");
        pipe.Cell(6).GetString().ShouldBe("м");
        pipe.Cell(7).GetValue<double>().ShouldBe(119.12);
        rows.Single(r => r.Cell(2).GetString().EndsWith("(запас)", StringComparison.Ordinal)).Cell(9).GetString().ShouldBe("запас по п. 6.1.15");

        // ГОСТ 21.110, п. 4.5: под заголовком раздела — пустая строка.
        var heading = rows.First(r => r.Cell(2).GetString() == "Оросители").RowNumber();
        ws.Row(heading + 1).Cell(2).GetString().ShouldBeEmpty();
        ws.Row(heading + 2).Cell(2).GetString().ShouldBe("1 Ороситель спринклерный водяной");
    }

    [Fact]
    public void Page_is_A3_landscape_with_repeated_header()
    {
        var ws = WriteAndRead().Worksheet(1);
        ws.PageSetup.PaperSize.ShouldBe(XLPaperSize.A3Paper);
        ws.PageSetup.PageOrientation.ShouldBe(XLPageOrientation.Landscape);
        ws.PageSetup.FirstRowToRepeatAtTop.ShouldBe(SpecificationXlsx.HeaderRow);
        ws.PageSetup.LastRowToRepeatAtTop.ShouldBe(SpecificationXlsx.HeaderRow + 1);
    }

    [Fact]
    public void Rows_are_8_mm_high_and_the_bottom_is_left_for_the_title_block()
    {
        var ws = WriteAndRead().Worksheet(1);
        var body = ws.RowsUsed().Where(r => r.RowNumber() > SpecificationXlsx.HeaderRow + 1).ToList();
        body.ShouldAllBe(r => Math.Abs((r.Height / SpecificationXlsx.RowHeightPt) - Math.Round(r.Height / SpecificationXlsx.RowHeightPt)) < 0.01 && r.Height >= SpecificationXlsx.RowHeightPt - 0.1);
        (ws.PageSetup.Margins.Bottom * 25.4).ShouldBeGreaterThanOrEqualTo(55);
    }
}
