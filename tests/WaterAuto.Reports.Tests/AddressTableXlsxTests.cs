using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Reports.Tests;

public class AddressTableXlsxTests
{
    [Fact]
    public void Writes_one_row_per_input()
    {
        var path = Path.Combine(Path.GetTempPath(), $"addr_{Guid.NewGuid():N}.xlsx");
        try
        {
            AddressRow[] rows =
            [
                new("С2000-КДЛ №1", "С2000-АР8 №1", 1, "ЗД1", "Задвижка DN150", "Положение запорного устройства"),
                new("С2000-КДЛ №1", "С2000-АР8 №1", 2, "УУ1", "Узел управления DN100", "Срабатывание сигнализатора давления"),
            ];

            AddressTableXlsx.Write(path, rows, "Объект. Таблица адресов");

            var sheet = XlsxPreview.ReadFirstSheet(path);
            sheet[AddressTableXlsx.HeaderRow - 1].ShouldBe(AddressTableXlsx.Columns, ignoreOrder: false);
            sheet[AddressTableXlsx.HeaderRow][3].ShouldBe("1");
            sheet[AddressTableXlsx.HeaderRow][4].ShouldBe("ЗД1");
            sheet[AddressTableXlsx.HeaderRow + 1][3].ShouldBe("2");
            sheet[AddressTableXlsx.HeaderRow + 1][6].ShouldBe("Срабатывание сигнализатора давления");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
