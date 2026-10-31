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
    public void No_sources_give_no_points_and_no_issues()
    {
        var result = ControlPoints.Build([]);

        result.Points.ShouldBeEmpty();
        result.Issues.ShouldBeEmpty();
    }
}
