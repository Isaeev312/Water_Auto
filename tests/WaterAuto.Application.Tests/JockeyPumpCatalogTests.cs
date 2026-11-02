using WaterAuto.Core;

namespace WaterAuto.Application.Tests;

/// <summary>Жокей-насос с мембранной ёмкостью по каталогу изготовителя: обозначение «ЖН-Q/H-V» и проверка рабочего диапазона.</summary>
public class JockeyPumpCatalogTests
{
    private static readonly JockeyPumpSeries Series = new("ЖН-{q}/{h}-{v}", "ТУ 28.13.14-204-00226827-2025",
        new Flow(0.45), new Flow(4.2), new Pressure(0.14), new Pressure(1.6), IncludesTank: true);

    [Fact]
    public void Catalog_file_is_read()
    {
        var series = PumpCatalog.JockeyPumps(RepoPaths.Norms);
        series.ShouldNotBeNull();
        series.Designation.ShouldContain("{q}");
        series.IncludesTank.ShouldBeTrue();
    }

    [Fact]
    public void Designation_takes_flow_in_m3h_head_in_m_and_tank_volume()
    {
        // 0,833 л/с = 3 м³/ч; 0,97 МПа = 97 м; бак 50 л — пример обозначения из РЭ изготовителя.
        var choice = Series.Choose(new Flow(0.8333), new Pressure(0.97), 50);
        choice.Designation.ShouldBe("ЖН-3/97-50");
        choice.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Out_of_range_gives_warning_but_keeps_designation()
    {
        var choice = Series.Choose(new Flow(5), new Pressure(1.8), 80);
        choice.Designation.ShouldBe("ЖН-18/180-80");
        choice.Issues.Select(i => i.Code).ShouldBe(["jockey.catalogFlow", "jockey.catalogPressure"]);
        choice.Issues.ShouldAllBe(i => i.Level == IssueLevel.Warning);
    }
}

public class JockeyPracticeTests
{
    private static readonly JockeyPractice Rule = new(0.5, 0.1, "ГРВ");

    [Fact]
    public void Unset_values_are_assigned_by_practice()
    {
        var j = ProjectJockey.Assign(null, Rule, 1.36, 0.62);
        j.FlowLitersPerSecond.ShouldBe(0.68);
        j.StartPressureMpa.ShouldBe(0.62);
        j.StopPressureMpa.ShouldBe(0.72);
        j.AssignedBy.ShouldNotBeNull();
    }

    [Fact]
    public void Manual_values_win_and_stop_alone_gives_start_below_it()
    {
        var j = ProjectJockey.Assign(new ProjectJockey { StopPressureMpa = 0.8 }, Rule, 1.36, 0.62);
        j.StartPressureMpa.ShouldBe(0.7);
        j.StopPressureMpa.ShouldBe(0.8);

        var all = new ProjectJockey { FlowLitersPerSecond = 1, StartPressureMpa = 0.5, StopPressureMpa = 0.6 };
        ProjectJockey.Assign(all, Rule, 1.36, 0.62).ShouldBe(all);
    }

    [Fact]
    public void Rule_file_is_read() => PumpCatalog.JockeyRule(RepoPaths.Norms).ShouldNotBeNull();
}
