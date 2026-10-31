using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// Команды СПС в системы вентиляции (АППЗ): отключение общеобменной, пуск противодымной по зонам, клапаны — СП 484 разд. 7, СП 7 пп. 6.24, 7.20.
public class HvacCommandsTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static readonly HvacInputs Inputs = new(
        GeneralVentilation: ["П-1", "П-2", "В-1", "В-2"],
        SmokeZones: [new("ДЗ-1", "ВДУ-1", "ПД-1", 2), new("ДЗ-2", "ВДУ-2", null, 1)],
        NormallyOpenDampers: 4);

    [Fact]
    public void General_ventilation_is_shut_down_and_normally_open_dampers_closed_on_fire()
    {
        var result = HvacCommands.Build(Inputs, Norms);

        var shutdown = result.Commands.Single(c => c.Kind == HvacCommandKind.StopGeneralVentilation);
        shutdown.Targets.ShouldBe(["П-1", "П-2", "В-1", "В-2"]);
        shutdown.Clause.ShouldContain("6.24");
        shutdown.Trigger.ShouldContain("любой ЗКСПС");
        result.Commands.Single(c => c.Kind == HvacCommandKind.CloseNormallyOpenDampers).Count.ShouldBe(4);
    }

    [Fact]
    public void Each_smoke_zone_gets_exhaust_then_supply_with_the_normative_lead_and_its_dampers()
    {
        var result = HvacCommands.Build(Inputs, Norms);

        var zone1 = result.Commands.Where(c => c.Zone == "ДЗ-1").ToList();
        zone1.Select(c => c.Kind).ShouldBe([HvacCommandKind.OpenSmokeDampers, HvacCommandKind.StartSmokeExhaust, HvacCommandKind.StartSmokeSupply]);
        zone1.Single(c => c.Kind == HvacCommandKind.OpenSmokeDampers).Count.ShouldBe(2);
        var supply = zone1.Single(c => c.Kind == HvacCommandKind.StartSmokeSupply);
        supply.Targets.ShouldBe(["ПД-1"]);
        supply.DelaySeconds.ShouldBe(20);
        supply.Clause.ShouldContain("7.20");
        zone1.Single(c => c.Kind == HvacCommandKind.StartSmokeExhaust).Trigger.ShouldContain("ДЗ-1");

        // Зона без подпора — без команды на приточную.
        result.Commands.Where(c => c.Zone == "ДЗ-2").ShouldNotContain(c => c.Kind == HvacCommandKind.StartSmokeSupply);
        result.Commands.Count.ShouldBe(2 + 3 + 2);
    }

    [Fact]
    public void Outputs_are_one_relay_per_command()
    {
        HvacCommands.Build(Inputs, Norms).Outputs.ShouldBe(7);
    }

    [Fact]
    public void Missing_norm_records_are_errors()
    {
        var result = HvacCommands.Build(Inputs, new EmptyNorms());

        result.Commands.ShouldBeEmpty();
        result.Issues.ShouldContain(i => i.Code == "aps.hvac.noNorm" && i.Level == IssueLevel.Error);
    }

    private sealed class EmptyNorms : INormsProvider
    {
        public NormRecord? Find(string id) => null;

        public IEnumerable<NormRecord> ByClause(string clause) => [];
    }
}
