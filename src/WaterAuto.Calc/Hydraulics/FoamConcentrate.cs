using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Пенообразователь: объём раствора, м³ (тушение и трубы), концентрат, резерв и всего, м³; диапазон дозирования.</summary>
public sealed record FoamConcentrateResult(double SolutionM3, double PipesM3, double ConcentrateM3, double ReserveM3, double TotalM3, double ConcentrationPercent, Flow DosingMin, Flow DosingMax, IReadOnlyList<Issue> Issues);

/// <summary>
/// Количество пенообразователя пенной АУП (СП 485, пп. 6.9.18, 6.9.19, 6.9.23): раствор W = Q·t·60/1000 + V_тр, пенообразователь —
/// W·c/100 при концентрации по ТД пенообразователя (проектировщик), резерв — доля 6.9.18 от расчётного; дозатор — от расхода одного
/// диктующего оросителя до расчётного расхода установки.
/// </summary>
public static class FoamConcentrate
{
    private const string Doc = "СП 485.1311500.2020";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static FoamConcentrateResult Calculate(Flow designFlow, double durationMin, double pipesM3, double concentrationPercent, Flow oneSprinkler, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        var reserve = norms.Find("sp485.p6_9_18.reserve");
        if (reserve is null || norms.Find("sp485.p6_9_19.pipes") is null)
        {
            issues.Add(new Issue("foam.noNorm", IssueLevel.Error, "В данных норм нет пп. 6.9.18, 6.9.19 (data/norms/sp485/clauses_6_9_foam.json)."));
            return new(0, 0, 0, 0, 0, 0, new Flow(0), new Flow(0), issues);
        }

        if (concentrationPercent <= 0 || concentrationPercent >= 100)
        {
            issues.Add(new Issue("foam.concentration", IssueLevel.Error,
                "Концентрация пенообразователя в растворе не задана (design.foam.concentration, %) — по ТД применяемого пенообразователя; количество не рассчитано.", Clause: Doc + ", п. 6.9.18"));
            return new(0, pipesM3, 0, 0, 0, concentrationPercent, oneSprinkler, designFlow, issues);
        }

        var solution = (designFlow.LitersPerSecond * durationMin * 60 / 1000) + pipesM3;
        var concentrate = solution * concentrationPercent / 100;
        var spare = concentrate * reserve.Values["reservePercent"]!.Value / 100;
        issues.Add(new Issue("foam.amount", IssueLevel.Info,
            string.Create(Ru, $"Раствор пенообразователя {solution:0.0} м³ (с трубами {pipesM3:0.0##} м³, п. 6.9.19); пенообразователь {concentrate:0.00} м³ при {concentrationPercent:0.#} %, резерв {spare:0.00} м³ (п. 6.9.18); дозирование — от {oneSprinkler.LitersPerSecond:0.00} до {designFlow.LitersPerSecond:0.00} л/с (п. 6.9.23)."),
            Clause: Doc + ", пп. 6.9.18, 6.9.19, 6.9.23"));
        return new(solution, pipesM3, concentrate, spare, concentrate + spare, concentrationPercent, oneSprinkler, designFlow, issues);
    }
}
