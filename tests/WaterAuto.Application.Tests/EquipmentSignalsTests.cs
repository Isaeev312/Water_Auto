using WaterAuto.Application;
using WaterAuto.Core;

namespace WaterAuto.Application.Tests;

// Сигналы оборудования АУПТ для АПС (ADR 0012, п. 2): берутся из поля signals шаблонов каталога, в коде их списка нет.
public class EquipmentSignalsTests
{
    private static string Catalog(string file) => EquipmentSet.PathFor(RepoPaths.Norms, file);

    [Fact]
    public void Pump_station_signals_follow_template_fields_and_counts()
    {
        var tokens = new Dictionary<string, string> { ["dnS"] = "150", ["dnD"] = "125", ["dnC"] = "100" };
        var counts = new Dictionary<string, int> { ["pumps"] = 2, ["sections"] = 3, ["inlets"] = 2, ["truckInlets"] = 2, ["extraSections"] = 2 };

        var signals = EquipmentSet.Signals(Catalog(EquipmentSet.PumpStationFile), tokens, counts);

        var cuValves = signals.Single(s => s.Role == "cuValve");
        cuValves.Kind.ShouldBe(SignalKind.ValvePosition);
        cuValves.Count.ShouldBe(6);
        cuValves.Equipment.ShouldContain("DN100");
        cuValves.Clause.ShouldBe("СП 485.1311500.2020, п. 6.8.7");

        var cu = signals.Single(s => s.Role == "cu");
        cu.Kind.ShouldBe(SignalKind.PressureSwitch);
        cu.Count.ShouldBe(3);

        // Манометры, обратные клапаны и вставки сигналов не дают.
        signals.ShouldNotContain(s => s.Role == "gauge" || s.Role == "checkD" || s.Role == "insertS");
    }

    [Fact]
    public void Pump_cabinet_gives_generic_start_fault_and_automatics_off_signals()
    {
        var counts = new Dictionary<string, int> { ["pumps"] = 2, ["sections"] = 1, ["inlets"] = 1 };

        var cabinet = EquipmentSet.Signals(Catalog(EquipmentSet.PumpStationFile), new Dictionary<string, string>(), counts)
            .Where(s => s.Role == "cabinet").ToList();

        cabinet.Select(s => s.Kind).ShouldBe([SignalKind.PumpStart, SignalKind.PumpFault, SignalKind.AutomaticsOff]);
        cabinet.ShouldAllBe(s => s.Count == 1);
        // Выходы «Пуск» и «Неисправность» — ГОСТ Р 53325-2012, п. 7.4.5; для «Автоматика отключена» пункт о передаче не найден.
        cabinet[0].Clause!.ShouldContain("7.4.5");
        cabinet[1].Clause!.ShouldContain("7.4.5");
        cabinet[2].Clause.ShouldBeNull();
    }

    [Fact]
    public void Pump_set_roles_drop_signals_of_rows_replaced_by_the_set()
    {
        var tokens = new Dictionary<string, string> { ["dnS"] = "150", ["dnD"] = "125", ["dnC"] = "100" };
        var counts = new Dictionary<string, int> { ["pumps"] = 2, ["sections"] = 1, ["inlets"] = 2 };

        var signals = EquipmentSet.Signals(Catalog(EquipmentSet.PumpStationFile), tokens, counts, EquipmentSet.PumpSetRoles);

        signals.ShouldNotContain(s => s.Role == "valveS" || s.Role == "valveD");
        signals.Single(s => s.Role == "inletValve").Count.ShouldBe(2);
        signals.ShouldContain(s => s.Role == "cuValve");
    }

    [Fact]
    public void Template_without_signals_gives_none_and_zero_count_is_skipped()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "set.json");
            File.WriteAllText(path, """
                { "items": [
                  { "name": "Манометр", "unit": "шт.", "per": 1 },
                  { "name": "Затвор DN{dn}", "unit": "шт.", "per": 2, "count": "sections", "signals": [ { "kind": "valvePosition" } ] }
                ] }
                """);

            EquipmentSet.Signals(path, new Dictionary<string, string> { ["dn"] = "80" }, new Dictionary<string, int> { ["sections"] = 0 }).ShouldBeEmpty();

            var one = EquipmentSet.Signals(path, new Dictionary<string, string> { ["dn"] = "80" }, new Dictionary<string, int> { ["sections"] = 1 }).ShouldHaveSingleItem();
            one.Equipment.ShouldBe("Затвор DN80");
            one.Count.ShouldBe(2);
            one.Clause.ShouldBeNull();
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Control_unit_signals_are_summed_per_dn()
    {
        var signals = EquipmentSet.Signals(Catalog(EquipmentSet.ControlUnitFile), [100, 100, 150]);

        signals.Where(s => s.Kind == SignalKind.PressureSwitch).Sum(s => s.Count).ShouldBe(3);
        signals.Single(s => s.Kind == SignalKind.ValvePosition && s.Equipment.Contains("DN100", StringComparison.Ordinal)).Count.ShouldBe(2);
        signals.Single(s => s.Kind == SignalKind.ValvePosition && s.Equipment.Contains("DN150", StringComparison.Ordinal)).Count.ShouldBe(1);
    }
}
