using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Reports.Tests;

public class SignalListXlsxTests
{
    [Fact]
    public void Writes_one_row_per_point_with_kind_text_and_clause()
    {
        var path = Path.Combine(Path.GetTempPath(), $"signals_{Guid.NewGuid():N}.xlsx");
        try
        {
            ControlPoint[] points =
            [
                new("cuValve.1", "Затвор DN100", SignalKind.ValvePosition, "СП 485.1311500.2020, п. 6.8.7", "cuValve", "ЗД3", 1),
                new("cu.1", "Узел управления DN100", SignalKind.PressureSwitch, null, "cu", "УУ1", 1),
                new("inletValve.1", "Задвижка DN150", SignalKind.ValvePosition, null, "inletValve", "ЗД1", null),
            ];

            SignalListXlsx.Write(path, points, "Объект. Перечень сигналов АУПТ в АПС", positions: new Dictionary<string, int> { ["cuValve"] = 14 });

            var rows = XlsxPreview.ReadFirstSheet(path);
            rows[0][0].ShouldBe("Объект. Перечень сигналов АУПТ в АПС");
            rows[SignalListXlsx.HeaderRow - 1].ShouldBe(SignalListXlsx.Columns, ignoreOrder: false);

            var first = rows[SignalListXlsx.HeaderRow];
            first[0].ShouldBe("1");
            first[1].ShouldBe("ЗД3");
            first[2].ShouldBe("14");
            first[3].ShouldBe("1");
            first[4].ShouldBe("Затвор DN100");
            first[5].ShouldBe("Положение запорного устройства");
            first[6].ShouldBe("СП 485.1311500.2020, п. 6.8.7");

            var second = rows[SignalListXlsx.HeaderRow + 1];
            second[2].ShouldBe("");
            second[5].ShouldBe("Срабатывание сигнализатора давления");
            second[6].ShouldBe("не указано");

            // Позиция вне секций — насосная.
            rows[SignalListXlsx.HeaderRow + 2][3].ShouldBe("насосная");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
