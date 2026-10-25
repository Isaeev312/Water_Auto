using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class HydrantDemandTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    // Стоянка: II степень, категория В, С0, 40 тыс. м³, высота здания 12 м, помещение 3,3 м; ПК DN50, спрыск 16, рукав 20 м.
    internal static readonly HydrantSettings Parking = new(
        FireResistance: "II",
        Category: "В",
        HazardClass: "С0",
        VolumeThousandM3: 40,
        BuildingHeight: new Length(12),
        RoomHeight: new Length(3.3),
        ValveDn: 50,
        NozzleMm: 16,
        HoseLength: 20,
        AboveAxis: new Length(1.35),
        PipeLength: new Length(15),
        PipeDn: 65);

    [Fact]
    public void Jets_and_minimum_flow_follow_table_Zh2()
    {
        var r = HydrantDemand.Resolve(Norms, Parking);
        r.Demand!.Jets.ShouldBe(2);
        r.Demand.MinJetFlow.LitersPerSecond.ShouldBe(2.5);
        r.Demand.NormIds.ShouldContain("snkr4004.tZh_2.I_II");
    }

    [Fact]
    public void Jet_height_is_at_least_the_7_9_minimum_and_Zh3_gives_flow_and_head()
    {
        // Помещение 3,3 м < 6 м → струя 6 м; DN50, спрыск 16, рукав 20 м: 2,6 л/с, 10,0 м.
        var d = HydrantDemand.Resolve(Norms, Parking).Demand!;
        d.RequiredJetHeight.Meters.ShouldBe(6);
        d.JetFlow.LitersPerSecond.ShouldBe(2.6);
        d.Head.Meters.ShouldBe(10.0);
    }

    [Fact]
    public void Row_is_taken_upwards_without_interpolation()
    {
        // Помещение 7 м → строка 8 м: 2,9 л/с, 13,0 м.
        var d = HydrantDemand.Resolve(Norms, Parking with { RoomHeight = new Length(7) }).Demand!;
        d.TableJetHeight.Meters.ShouldBe(8);
        d.JetFlow.LitersPerSecond.ShouldBe(2.9);
        d.Head.Meters.ShouldBe(13.0);
    }

    [Fact]
    public void Dash_cell_is_an_error()
    {
        // Спрыск 13 мм при струе 6 м — «--».
        var r = HydrantDemand.Resolve(Norms, Parking with { NozzleMm = 13 });
        r.Demand.ShouldBeNull();
        r.Issues.ShouldContain(i => i.Code == "vpv.noTableValue" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Stream_flow_below_Zh2_is_an_error()
    {
        // IV, В, С0, свыше 150 тыс. м³ → 2×5 л/с; ствол 16 мм даёт 2,6 л/с.
        var r = HydrantDemand.Resolve(Norms, Parking with { FireResistance = "IV", VolumeThousandM3 = 200 });
        r.Issues.ShouldContain(i => i.Code == "vpv.jetFlowLow" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Not_required_gives_no_demand_and_an_info()
    {
        var r = HydrantDemand.Resolve(Norms, Parking with { Category = "Д", FireResistance = "III" });
        r.Demand.ShouldBeNull();
        r.Issues.ShouldContain(i => i.Code == "vpv.notRequired" && i.Level == IssueLevel.Info);
    }

    [Fact]
    public void Valve_dn_is_checked_against_the_7_9_note()
    {
        // 4,6 л/с > 4 л/с при DN50 — предупреждение (допускается при ТЭО).
        var r = HydrantDemand.Resolve(Norms, Parking with { NozzleMm = 19, RoomHeight = new Length(9) });
        r.Demand!.JetFlow.LitersPerSecond.ShouldBe(4.6);
        r.Issues.ShouldContain(i => i.Code == "vpv.valveDn" && i.Level == IssueLevel.Warning);
    }
}
