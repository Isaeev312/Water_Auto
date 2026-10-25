using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Layout;

/// <summary>Номинальная температура срабатывания оросителя и её маркировка.</summary>
public sealed record TemperatureChoice(int Nominal, int MaxAmbient, string Color, int ResponseTime, IReadOnlyList<string> NormIds);

public sealed record TemperatureResult(TemperatureChoice? Choice, IReadOnlyList<Issue> Issues);

/// <summary>
/// Номинальная температура срабатывания спринклерного оросителя (п. 6.2.16 СП 485): наименьшая из табл. 6.4, для которой
/// предельно допустимая температура среды не ниже максимальной температуры в зоне оросителей (п. 6.2.17 — её задаёт
/// проектировщик); если задан набор температур модели из каталога — только из него. Цвет колбы (дужек) и время
/// срабатывания — по табл. 2 ГОСТ Р 51043.
/// </summary>
public static class SprinklerTemperature
{
    public static TemperatureResult Choose(INormsProvider norms, double? maxAmbient, IReadOnlyCollection<int>? available)
    {
        ArgumentNullException.ThrowIfNull(norms);
        if (maxAmbient is not { } t)
        {
            return new TemperatureResult(null, [new Issue(
                "sprinkler.temperature",
                IssueLevel.Warning,
                "Не задана максимальная температура среды в зоне оросителей (design.maxAmbientTemperature) — номинальная температура срабатывания не выбрана (пп. 6.2.16, 6.2.17).",
                Clause: "6.2.16")]);
        }

        var rows = norms.ByClause("табл. 6.4")
            .Where(r => r.Doc.StartsWith("СП 485", StringComparison.Ordinal))
            .Select(r => (Row: r, Nominal: (int)r.Values["nominal"]!.Value, MaxAmbient: (int)r.Values["maxAmbient"]!.Value))
            .Where(x => x.MaxAmbient >= t - 1e-9 && (available is null || available.Contains(x.Nominal)))
            .OrderBy(x => x.Nominal)
            .ToList();
        if (rows.Count == 0)
        {
            var list = available is null ? "" : $" Температуры модели: {string.Join(", ", available)} °С.";
            return new TemperatureResult(null, [new Issue(
                "sprinkler.temperature",
                IssueLevel.Error,
                string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Для температуры среды {t:0.#} °С нет подходящей номинальной температуры срабатывания (табл. 6.4).{list}"),
                Clause: "6.2.16")]);
        }

        var chosen = rows[0];
        var mark = norms.Find($"gost51043.t2.t{chosen.Nominal}");
        return new TemperatureResult(
            new TemperatureChoice(
                chosen.Nominal,
                chosen.MaxAmbient,
                mark?.Attributes.GetValueOrDefault("color") ?? "",
                (int)(mark?.Values.GetValueOrDefault("responseTime") ?? 0),
                mark is null ? [chosen.Row.Id] : [chosen.Row.Id, mark.Id]),
            []);
    }
}
