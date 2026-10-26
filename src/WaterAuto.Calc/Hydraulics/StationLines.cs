using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

public sealed record StationLinesResult(int RequiredSuctionLines, int RequiredPressureLines, bool TwoInputsRing, IReadOnlyList<Issue> Issues);

/// <summary>
/// Число трубопроводов насосной: входные всасывающие — не менее нормы (п. 6.10.27); напорные — не менее нормы, кроме случая
/// малого числа узлов управления и ПК (п. 6.10.28); два ввода и закольцованный подводящий при многих УУ или ПК (п. 6.7.1.43).
/// </summary>
public static class StationLines
{
    public static StationLinesResult Check(INormsProvider norms, int suctionLines, int pressureLines, int controlUnits, int hydrants)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var suction = Need(norms, "sp485.p6_10_27.suctionLines").Values;
        var pressure = Need(norms, "sp485.p6_10_28.pressureLines").Values;
        var inputs = Need(norms, "sp485.p6_7_1_43.twoInputs").Values;
        var issues = new List<Issue>();

        var needSuction = (int)suction["minCount"]!.Value;
        if (suctionLines < needSuction)
        {
            issues.Add(new Issue("pump.suctionLines", IssueLevel.Error, $"К насосной установке подведено {suctionLines} всасывающих трубопроводов, нужно не менее {needSuction}, каждый на полный расход: отметьте второй ввод на слое ПТ_НС_ВВОД.", Clause: "6.10.27"));
        }

        var reduced = controlUnits <= pressure["reducedMaxControlUnits"]!.Value && hydrants < pressure["reducedMaxHydrantsExclusive"]!.Value;
        var needPressure = reduced ? 1 : (int)pressure["minCount"]!.Value;
        if (pressureLines < needPressure)
        {
            issues.Add(new Issue("pump.pressureLines", IssueLevel.Error, $"Из насосной выходит {pressureLines} напорных трубопроводов, нужно не менее {needPressure} ({controlUnits} УУ, {hydrants} ПК): отметьте выпуски на слое ПТ_НС_ВЫПУСК.", Clause: "6.10.28"));
        }
        else if (!reduced)
        {
            issues.Add(new Issue("pump.pressureLines", IssueLevel.Info, $"Напорных трубопроводов — не менее {needPressure}, каждый на полный расход ({controlUnits} УУ, {hydrants} ПК).", Clause: "6.10.28"));
        }

        var ring = controlUnits >= inputs["minControlUnits"]!.Value || hydrants > inputs["maxHydrants"]!.Value;
        if (ring)
        {
            issues.Add(new Issue("pump.twoInputs", IssueLevel.Warning, $"При {controlUnits} УУ и {hydrants} ПК нужны два ввода, подводящий трубопровод закольцовывается.", Clause: "6.7.1.43"));
        }

        return new StationLinesResult(needSuction, needPressure, ring, issues);
    }

    private static NormRecord Need(INormsProvider norms, string id) =>
        norms.Find(id) ?? throw new InvalidOperationException($"В данных норм нет записи {id}.");
}
