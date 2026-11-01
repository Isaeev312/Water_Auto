using WaterAuto.Calc.Routing;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class PipeJointsTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    [Fact]
    public void Grooved_from_the_threshold_up_to_DN200_and_threaded_below()
    {
        // ГОСТ 3262 допускает разъёмные муфты (п. 6.7.2.1); ниже порога проектировщика — резьба.
        var dns = new Dictionary<int, string> { [25] = "ГОСТ 3262-75", [50] = "ГОСТ 3262-75", [150] = "ГОСТ 3262-75" };
        var r = PipeJoints.Resolve(dns, JointKind.Grooved, groovedMinDn: 50, Norms);
        r.ByDn[25].ShouldBe(JointKind.Threaded);
        r.ByDn[50].ShouldBe(JointKind.Grooved);
        r.ByDn[150].ShouldBe(JointKind.Grooved);
        r.Issues.ShouldNotContain(i => i.Level >= IssueLevel.Warning);
    }

    [Fact]
    public void Electric_welded_pipes_cannot_take_couplings_by_6_7_2_1()
    {
        var r = PipeJoints.Resolve(new Dictionary<int, string> { [80] = "ГОСТ 10704-91" }, JointKind.Grooved, 50, Norms);
        r.ByDn[80].ShouldBe(JointKind.Welded);
        r.Issues.ShouldContain(i => i.Code == "joints.standard" && i.Level == IssueLevel.Warning && i.Clause!.Contains("6.7.2.1"));
    }

    [Fact]
    public void Couplings_stop_at_DN200_by_6_7_2_6()
    {
        var r = PipeJoints.Resolve(new Dictionary<int, string> { [200] = "ГОСТ 8732-78", [250] = "ГОСТ 8732-78" }, JointKind.Grooved, 50, Norms);
        r.ByDn[200].ShouldBe(JointKind.Grooved);
        r.ByDn[250].ShouldBe(JointKind.Welded);
        r.Issues.ShouldContain(i => i.Code == "joints.maxDn" && i.Clause!.Contains("6.7.2.6"));
    }

    [Fact]
    public void Welded_choice_keeps_every_DN_welded()
    {
        var r = PipeJoints.Resolve(new Dictionary<int, string> { [25] = "ГОСТ 3262-75", [100] = "ГОСТ 10704-91" }, JointKind.Welded, 50, Norms);
        r.ByDn.Values.ShouldAllBe(k => k == JointKind.Welded);
        r.Issues.ShouldNotContain(i => i.Level >= IssueLevel.Warning);
    }
    [Fact]
    public void Threaded_joints_need_a_pipe_standard_that_allows_them()
    {
        // п. 6.7.2.1: трубы ГОСТ 10704 — только сварка и фланцы, резьба на них тоже недопустима.
        var r = PipeJoints.Resolve(new Dictionary<int, string> { [32] = "ГОСТ 10704-91" }, JointKind.Grooved, 50, Norms);
        r.ByDn[32].ShouldBe(JointKind.Welded);
        r.Issues.ShouldContain(i => i.Code == "joints.standard");
    }

    [Fact]
    public void Detachable_joints_remind_of_rooms_A_B_and_concealed_runs()
    {
        // Категорию помещений и скрытую прокладку программа не знает: замечание-напоминание со ссылками на пп. 6.7.2.5 и 6.7.1.8.
        var r = PipeJoints.Resolve(new Dictionary<int, string> { [50] = "ГОСТ 3262-75" }, JointKind.Grooved, 50, Norms);
        r.Issues.ShouldContain(i => i.Code == "joints.check" && i.Level == IssueLevel.Info && i.Message.Contains("6.7.2.5") && i.Message.Contains("6.7.1.8"));
        r.NormIds.ShouldContain("sp485.p6_7_2_5.categoryAB");
    }
}
