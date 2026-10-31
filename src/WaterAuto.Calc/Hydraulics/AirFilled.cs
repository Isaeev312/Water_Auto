using System.Globalization;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Воздухозаполненная сеть: объём воздуха в питающих и распределительных трубах, м³, уклоны по 6.7.1.21, замечания.</summary>
public sealed record AirFilledResult(double AirVolumeM3, double SlopeSmall, double SlopeLarge, int LargeFromDn, IReadOnlyList<Issue> Issues);

/// <summary>
/// Спринклерная воздухозаполненная АУП (СП 485 пп. 6.2.4–6.2.8, 6.2.19, 6.7.1.21): объём воздуха сети — для компрессора и времени
/// заполнения (6.2.6, не более 1 ч); оросители при температуре ниже порога 6.2.19 — только розеткой вверх или горизонтально;
/// уклон труб к спуску. Инерционность (6.2.4, не более 180 с) свод не нормирует методикой — замечание с требованием подтвердить
/// по ТД узла управления и акселератора.
/// </summary>
public static class AirFilled
{
    private const string Doc = "СП 485.1311500.2020";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static AirFilledResult Check(IReadOnlyList<PipeRoute> routes, PipeSeries series, string deflector, double? minTemperatureC, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        var slope = norms.Find("sp485.p6_7_1_21.slope");
        var delay = norms.Find("sp485.p6_2_4.airDelay");
        var fill = norms.Find("sp485.p6_2_6.airFill");
        var deflectorRule = norms.Find("sp485.p6_2_19.airDeflector");
        if (slope is null || delay is null || fill is null || deflectorRule is null)
        {
            issues.Add(new Issue("air.noNorm", IssueLevel.Error, "В данных норм нет записей воздухозаполненной АУП (data/norms/sp485/clauses_6_2_air.json)."));
            return new(0, 0, 0, 0, issues);
        }

        var volume = 0.0;
        foreach (var s in routes.SelectMany(r => r.Segments.Concat(r.Loops)))
        {
            if (series.Pick(s.Dn) is not { } pipe)
            {
                issues.Add(new Issue("air.noPipe", IssueLevel.Warning, $"DN{s.Dn} нет в сортаменте — участок {s.Id} не вошёл в объём воздуха.", s.Id));
                continue;
            }

            var d = (pipe.OuterDiameterMm - (2 * pipe.WallMm)) / 1000;
            volume += Math.PI / 4 * d * d * s.Length.Meters;
        }

        if (minTemperatureC is { } t && t < deflectorRule.Values["belowTemperature"]!.Value && deflector.Equals("down", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new Issue("air.deflector", IssueLevel.Error,
                string.Create(Ru, $"Воздухозаполненная АУП при {t:0} °С: оросители розеткой вниз недопустимы — только вверх или горизонтально (п. 6.2.19)."), Clause: Doc + ", п. 6.2.19"));
        }

        issues.Add(new Issue("air.delay", IssueLevel.Warning,
            string.Create(Ru, $"Воздухозаполненная АУП: время от срабатывания диктующего оросителя до подачи воды — не более {delay.Values["maxDelay"]:0} с (п. 6.2.4); методики в своде нет — подтвердить по ТД узла управления (акселератор, эксгаустер) для объёма воздуха {volume:0.###} м³."),
            Clause: Doc + ", п. 6.2.4"));
        issues.Add(new Issue("air.fill", IssueLevel.Info,
            string.Create(Ru, $"Объём воздуха в питающих и распределительных трубопроводах {volume:0.###} м³; компрессор (или баллоны, п. 6.1.7) — с заполнением до рабочего давления не дольше {fill.Values["maxFill"]:0} ч (п. 6.2.6)."),
            Clause: Doc + ", п. 6.2.6"));
        return new(volume, slope.Values["slopeSmall"]!.Value, slope.Values["slopeLarge"]!.Value, (int)slope.Values["largeFromDn"]!.Value, issues);
    }
}
