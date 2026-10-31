using WaterAuto.Application;

namespace WaterAuto.Application.Tests;

// Каталог приборов питания для расчёта АКБ (ADR 0012, п. 5): числа — из паспортов, одно напряжение на набор.
public class AlarmPowerCatalogTests
{
    [Fact]
    public void Repository_catalog_loads_with_passport_values()
    {
        var set = AlarmPowerCatalog.Load(EquipmentSet.PathFor(RepoPaths.Norms, AlarmPowerCatalog.File));

        set.Voltage.ShouldBe(new WaterAuto.Core.Voltage(12));
        set.Controller.Idle.Amperes.ShouldBe(0.08);
        set.Module.Inputs.ShouldBe(8);
        set.Supply.OwnCurrent.Amperes.ShouldBe(0.04);
        set.Battery.Nominal.AmpereHours.ShouldBe(17);
        set.Battery.Discharge[TimeSpan.FromHours(24)].Amperes.ShouldBe(0.727);
        set.Battery.Discharge[TimeSpan.FromHours(1)].Amperes.ShouldBe(10.41);
        set.Detectors["smoke"].LoopCurrent.Amperes.ShouldBe(0.0006);
        set.Detectors["manual"].Name.ShouldBe("ИПР 513-3АМ");
    }

    [Fact]
    public void Device_voltage_other_than_the_set_voltage_is_rejected()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var text = File.ReadAllText(EquipmentSet.PathFor(RepoPaths.Norms, AlarmPowerCatalog.File))
                .Replace("\"voltage\": 12,\n    \"ownCurrent\"", "\"voltage\": 24,\n    \"ownCurrent\"", StringComparison.Ordinal)
                .Replace("\"voltage\": 12,\r\n    \"ownCurrent\"", "\"voltage\": 24,\r\n    \"ownCurrent\"", StringComparison.Ordinal);
            text.ShouldContain("\"voltage\": 24");
            var path = Path.Combine(dir.FullName, "alarm_power.json");
            File.WriteAllText(path, text);

            Should.Throw<InvalidDataException>(() => AlarmPowerCatalog.Load(path)).Message.ShouldContain("24");
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
