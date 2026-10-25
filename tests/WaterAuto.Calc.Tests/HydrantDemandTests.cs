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
        // Спрыск 19 мм при DN50 и струе 14 м — «--».
        var r = HydrantDemand.Resolve(Norms, Parking with { NozzleMm = 19, RoomHeight = new Length(13) });
        r.Demand.ShouldBeNull();
        r.Issues.ShouldContain(i => i.Code == "vpv.noTableValue" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Stream_flow_below_Zh2_is_an_error()
    {
        // IV, В, С0, свыше 150 тыс. м³ → 2×5 л/с; ствол 13 мм при DN50 даёт не более 3,6 л/с ни при какой высоте струи.
        var r = HydrantDemand.Resolve(Norms, Parking with { FireResistance = "IV", VolumeThousandM3 = 200, NozzleMm = 13, RoomHeight = new Length(11) });
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

    [Fact]
    public void Latin_look_alike_letters_are_accepted()
    {
        HydrantDemand.Resolve(Norms, Parking with { Category = "B", HazardClass = "C0" }).Demand.ShouldNotBeNull();
    }

    [Fact]
    public void Parking_jets_follow_SNiP_21_02_6_4()
    {
        // Отсек 40 тыс. м³ > 5 → 2 струи по 5 л/с; ствол 19 мм, DN65, рукав 20 м: первая строка Ж.3 с q ≥ 5 — 12 м, 5,2 л/с, 19,9 м.
        var r = HydrantDemand.Resolve(Norms, Parking with { IsParking = true, ValveDn = 65, NozzleMm = 19 });
        var d = r.Demand!;
        d.Jets.ShouldBe(2);
        d.MinJetFlow.LitersPerSecond.ShouldBe(5);
        d.TableJetHeight.Meters.ShouldBe(12);
        d.JetFlow.LitersPerSecond.ShouldBe(5.2);
        d.Head.Meters.ShouldBe(19.9);
        d.Source.ShouldContain("СНиП 21-02-99*");
    }

    [Fact]
    public void Small_parking_takes_two_jets_of_2_5()
    {
        var d = HydrantDemand.Resolve(Norms, Parking with { IsParking = true, VolumeThousandM3 = 4 }).Demand!;
        d.MinJetFlow.LitersPerSecond.ShouldBe(2.5);
        d.JetFlow.LitersPerSecond.ShouldBe(2.6);
    }

    [Fact]
    public void Public_building_row_of_Zh1_is_chosen_by_its_id()
    {
        var d = HydrantDemand.Resolve(Norms, Parking with { Zh1Row = "g4.seats_over300" }).Demand!;
        d.Jets.ShouldBe(2);
        d.MinJetFlow.LitersPerSecond.ShouldBe(2.5);
        d.Source.ShouldContain("табл. Ж.1");
        d.NormIds.ShouldContain("snkr4004.tZh_1.g4.seats_over300");
    }

    [Fact]
    public void Hall_with_combustible_finish_takes_one_jet_more_7_7()
    {
        var d = HydrantDemand.Resolve(Norms, Parking with { Zh1Row = "g4.seats_over300", ExtraJet = true }).Demand!;
        d.Jets.ShouldBe(3);
        d.NormIds.ShouldContain("snkr4004.p7_7.extraJet");
    }

    [Fact]
    public void Unknown_Zh1_row_is_an_error()
    {
        HydrantDemand.Resolve(Norms, Parking with { Zh1Row = "g9.none" }).Issues
            .ShouldContain(i => i.Code == "vpv.noRow" && i.Level == IssueLevel.Error);
    }
}
