using WaterAuto.Alarm;
using WaterAuto.Core;

namespace WaterAuto.Alarm.Tests;

// Этап «Точки управления» (ADR 0012, п. 2): одна точка на каждое устройство источника, обозначение — от роли позиции АУПТ.
public class ControlPointsTests
{
    [Fact]
    public void Each_device_becomes_a_point_with_a_stable_id()
    {
        SignalSource[] sources =
        [
            new("cuValve", "Затвор DN100", SignalKind.ValvePosition, 2, "СП 485.1311500.2020, п. 6.8.7"),
            new("cu", "Узел управления DN100", SignalKind.PressureSwitch, 1),
        ];

        var result = ControlPoints.Build(sources);

        result.Points.Select(p => p.Id).ShouldBe(["cuValve.1", "cuValve.2", "cu.1"]);
        result.Points[0].Equipment.ShouldBe("Затвор DN100");
        result.Points[0].Clause.ShouldBe("СП 485.1311500.2020, п. 6.8.7");
        result.Points[2].Kind.ShouldBe(SignalKind.PressureSwitch);
    }

    [Fact]
    public void Several_signals_of_one_role_get_the_kind_in_the_id()
    {
        SignalSource[] sources =
        [
            new("cabinet", "Шкаф управления пожарными насосами", SignalKind.PumpStart, 1, "ГОСТ Р 53325-2012, п. 7.4.5"),
            new("cabinet", "Шкаф управления пожарными насосами", SignalKind.PumpFault, 1, "ГОСТ Р 53325-2012, п. 7.4.5"),
            new("cu", "Узел управления DN100", SignalKind.PressureSwitch, 1, "п. 1"),
        ];

        var result = ControlPoints.Build(sources);

        result.Points.Select(p => p.Id).ShouldBe(["cabinet.pumpStart.1", "cabinet.pumpFault.1", "cu.1"]);
    }

    [Fact]
    public void Ids_do_not_depend_on_other_sources()
    {
        var alone = ControlPoints.Build([new SignalSource("cu", "Узел управления DN100", SignalKind.PressureSwitch, 2)]);
        var withValves = ControlPoints.Build(
        [
            new SignalSource("inletValve", "Задвижка DN150", SignalKind.ValvePosition, 2),
            new SignalSource("cu", "Узел управления DN100", SignalKind.PressureSwitch, 2),
        ]);

        withValves.Points.Where(p => p.Kind == SignalKind.PressureSwitch).Select(p => p.Id).ShouldBe(alone.Points.Select(p => p.Id));
    }

    [Fact]
    public void Sources_without_role_get_numbered_by_kind_and_do_not_collide()
    {
        var result = ControlPoints.Build(
        [
            new SignalSource(null, "Затвор DN100", SignalKind.ValvePosition, 1),
            new SignalSource(null, "Затвор DN150", SignalKind.ValvePosition, 1),
        ]);

        result.Points.Select(p => p.Id).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void Missing_clause_is_reported_once_per_source()
    {
        var result = ControlPoints.Build(
        [
            new SignalSource("valveS", "Затвор DN150", SignalKind.ValvePosition, 2),
            new SignalSource("cuValve", "Затвор DN100", SignalKind.ValvePosition, 2, "СП 485.1311500.2020, п. 6.8.7"),
        ]);

        var noClause = result.Issues.Where(i => i.Code == "aps.signal.noClause").ShouldHaveSingleItem();
        noClause.ElementId.ShouldBe("valveS");
        noClause.Level.ShouldBe(IssueLevel.Info);
    }

    [Fact]
    public void Designations_run_through_one_series_per_mark()
    {
        var result = ControlPoints.Build(
        [
            new SignalSource("inletValve", "Задвижка DN150", SignalKind.ValvePosition, 2, "п. 1", Mark: "ЗД"),
            new SignalSource("cuValve", "Затвор DN100", SignalKind.ValvePosition, 2, "п. 2", Mark: "ЗД"),
        ]);

        result.Points.Select(p => p.Designation).ShouldBe(["ЗД1", "ЗД2", "ЗД3", "ЗД4"]);
        result.Points.Select(p => p.Role).ShouldBe(["inletValve", "inletValve", "cuValve", "cuValve"]);
    }

    [Fact]
    public void Single_device_has_no_number_and_its_signals_share_the_designation()
    {
        var result = ControlPoints.Build(
        [
            new SignalSource("cabinet", "Шкаф", SignalKind.PumpStart, 1, "п. 1", Mark: "ШУН"),
            new SignalSource("cabinet", "Шкаф", SignalKind.PumpFault, 1, "п. 1", Mark: "ШУН"),
        ]);

        result.Points.Select(p => p.Designation).ShouldBe(["ШУН", "ШУН"]);
    }

    [Fact]
    public void Section_bound_devices_get_the_section_number_in_plan_order()
    {
        // Две секции: на секцию узел управления и два затвора (до и после узла), как на схеме насосной.
        var result = ControlPoints.Build(
        [
            new SignalSource("cu", "Узел управления DN100", SignalKind.PressureSwitch, 2, "п. 1", Mark: "УУ", PerSection: 1),
            new SignalSource("cuValve", "Затвор DN100", SignalKind.ValvePosition, 4, "п. 2", Mark: "ЗД", PerSection: 2),
            new SignalSource("inletValve", "Задвижка DN150", SignalKind.ValvePosition, 1, "п. 3", Mark: "ЗД"),
        ]);

        result.Points.Select(p => (p.Designation, p.Section)).ShouldBe(
        [
            ("УУ1", (int?)1), ("УУ2", 2),
            ("ЗД1", 1), ("ЗД2", 1), ("ЗД3", 2), ("ЗД4", 2),
            ("ЗД5", null),
        ]);
    }

    [Fact]
    public void Source_without_mark_keeps_the_id_as_designation()
    {
        var point = ControlPoints.Build([new SignalSource("cu", "Узел", SignalKind.PressureSwitch, 1, "п. 1")]).Points.ShouldHaveSingleItem();

        point.Designation.ShouldBe("cu.1");
        point.Section.ShouldBeNull();
    }

    [Fact]
    public void No_sources_give_no_points_and_no_issues()
    {
        var result = ControlPoints.Build([]);

        result.Points.ShouldBeEmpty();
        result.Issues.ShouldBeEmpty();
    }
}
