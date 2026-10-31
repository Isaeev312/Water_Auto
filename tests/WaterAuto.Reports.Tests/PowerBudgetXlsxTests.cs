using WaterAuto.Core;
using WaterAuto.Reports;

namespace WaterAuto.Reports.Tests;

public class PowerBudgetXlsxTests
{
    [Fact]
    public void Writes_devices_and_battery_result()
    {
        var path = Path.Combine(Path.GetTempPath(), $"battery_{Guid.NewGuid():N}.xlsx");
        try
        {
            var battery = new BatteryModel("АБ 1217С", new Charge(17), 80, new Dictionary<double, Current> { [1] = new(10.41), [24] = new(0.727) }, "РЭ АБ");
            var budget = new PowerBudget(12,
                [
                    new PowerLine("С2000-КДЛ", 1, new Current(0.085), new Current(0.4), "дежурный — по нагрузке линии", "РЭ КДЛ"),
                    new PowerLine("С2000-АР8", 1, null, null, "питание от линии", "РЭ АР8"),
                ],
                new Current(0.085), new Current(0.4), new Current(0.04), battery, 0.974, 1.633, new Charge(4.553), TimeSpan.FromHours(34));

            PowerBudgetXlsx.Write(path, budget, "Объект. Расчёт ёмкости АКБ");

            var rows = XlsxPreview.ReadFirstSheet(path);
            rows[0][0].ShouldBe("Объект. Расчёт ёмкости АКБ");
            rows[PowerBudgetXlsx.HeaderRow - 1].ShouldBe(PowerBudgetXlsx.Columns, ignoreOrder: false);
            rows[PowerBudgetXlsx.HeaderRow][1].ShouldBe("С2000-КДЛ");
            rows[PowerBudgetXlsx.HeaderRow][3].ShouldBe("85");
            rows[PowerBudgetXlsx.HeaderRow + 1][3].ShouldBe("—");
            var flat = rows.SelectMany(r => r).ToList();
            flat.ShouldContain("Требуемая ёмкость АКБ, А·ч");
            flat.ShouldContain("4,55");
            flat.ShouldContain("34,0");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
