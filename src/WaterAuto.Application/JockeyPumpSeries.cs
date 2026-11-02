using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>Жокей-насос по каталогу изготовителя: обозначение для спецификации и замечания о выходе за рабочий диапазон.</summary>
public sealed record JockeyPumpChoice(string Designation, string Document, bool IncludesTank, IReadOnlyList<Issue> Issues);

/// <summary>
/// Серия жокей-насосов изготовителя (data/catalog/jockey_pumps_*.json): шаблон обозначения ({q} — подача, м³/ч; {h} — напор, м;
/// {v} — вместимость мембранной ёмкости, л), документ и рабочий диапазон. Расход и уставки — решение проектировщика (В-07).
/// </summary>
public sealed record JockeyPumpSeries(string Designation, string Document, Flow FlowMin, Flow FlowMax, Pressure PressureMin, Pressure PressureMax, bool IncludesTank)
{
    public JockeyPumpChoice Choose(Flow flow, Pressure stop, double tankLiters)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var name = Designation
            .Replace("{q}", (flow.LitersPerSecond * 3.6).ToString("0.#", ru), StringComparison.Ordinal)
            .Replace("{h}", stop.ToHeight().Meters.ToString("0", ru), StringComparison.Ordinal)
            .Replace("{v}", tankLiters.ToString("0", ru), StringComparison.Ordinal);
        var issues = new List<Issue>();
        if (flow.LitersPerSecond < FlowMin.LitersPerSecond || flow.LitersPerSecond > FlowMax.LitersPerSecond)
        {
            issues.Add(new Issue("jockey.catalogFlow", IssueLevel.Warning, string.Create(ru,
                $"Расход жокей-насоса {flow.LitersPerSecond:0.##} л/с вне диапазона изготовителя {FlowMin.LitersPerSecond:0.##}–{FlowMax.LitersPerSecond:0.##} л/с: уточните по опросному листу.")));
        }

        if (stop.Mpa < PressureMin.Mpa || stop.Mpa > PressureMax.Mpa)
        {
            issues.Add(new Issue("jockey.catalogPressure", IssueLevel.Warning, string.Create(ru,
                $"Давление отключения жокей-насоса {stop.Mpa:0.###} МПа вне диапазона изготовителя {PressureMin.Mpa:0.##}–{PressureMax.Mpa:0.##} МПа: уточните по опросному листу.")));
        }

        return new JockeyPumpChoice(name, Document, IncludesTank, issues);
    }
}
