using WaterAuto.Calc.Hydraulics;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class StationLinesTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    [Fact]
    public void One_suction_line_is_an_error_6_10_27()
    {
        var r = StationLines.Check(Norms, suctionLines: 1, pressureLines: 1, controlUnits: 1, hydrants: 11);
        r.Issues.ShouldContain(i => i.Code == "pump.suctionLines" && i.Clause == "6.10.27");
        StationLines.Check(Norms, 2, 1, 1, 11).Issues.ShouldNotContain(i => i.Code == "pump.suctionLines");
    }

    [Theory]
    [InlineData(1, 11, 1)] // не более 3 УУ и менее 13 ПК — допускается один
    [InlineData(3, 12, 1)]
    [InlineData(4, 0, 2)]
    [InlineData(1, 13, 2)]
    public void Pressure_lines_follow_6_10_28(int units, int hydrants, int required)
    {
        StationLines.Check(Norms, 2, 1, units, hydrants).RequiredPressureLines.ShouldBe(required);
        StationLines.Check(Norms, 2, 1, units, hydrants).Issues.Any(i => i.Code == "pump.pressureLines").ShouldBe(required > 1);
    }

    [Fact]
    public void Four_control_units_need_two_inputs_and_a_ring_6_7_1_43()
    {
        StationLines.Check(Norms, 2, 2, 4, 0).Issues.ShouldContain(i => i.Code == "pump.twoInputs");
        StationLines.Check(Norms, 2, 1, 3, 12).Issues.ShouldNotContain(i => i.Code == "pump.twoInputs");
    }
}
