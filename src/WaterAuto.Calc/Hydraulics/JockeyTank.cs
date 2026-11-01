using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Подобранный бак жокей-насоса: модель, давление воздуха и доля воды при давлении отключения.</summary>
public sealed record JockeyTankResult(MembraneTank? Tank, Pressure? Precharge, double? WaterShare, IReadOnlyList<string> NormIds, IReadOnlyList<Issue> Issues);

/// <summary>
/// Мембранная ёмкость жокей-насоса (п. 6.9.4 СП 485): вместимость не менее нормы, вода — в доле нормы. Бак — наименьший по вместимости
/// из каталога, PN которого не ниже давления в системе; давление воздуха — чтобы при отключении жокей-насоса вода занимала середину
/// нормативной доли (изотермическое сжатие воздуха, абсолютные давления).
/// </summary>
public static class JockeyTank
{
    // Нормальное атмосферное давление, МПа — физическая постоянная для перевода в абсолютное давление.
    private const double Atmosphere = 0.101325;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static JockeyTankResult Select(IReadOnlyList<MembraneTank> catalog, INormsProvider norms, Pressure systemPressure, Pressure? stop, Pressure? start)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(norms);
        var rule = norms.Find("sp485.p6_9_4.jockeyTank")!;
        var minVolume = rule.Values["minVolume"]!.Value;
        var shareMin = rule.Values["waterShareMin"]!.Value / 100;
        var shareMax = rule.Values["waterShareMax"]!.Value / 100;
        var issues = new List<Issue>();

        // PN — в барах: 1 МПа = 10 бар.
        var tank = catalog
            .Where(t => t.VolumeL >= minVolume - 1e-9 && t.PnBar / 10 >= systemPressure.Mpa - 1e-9)
            .OrderBy(t => t.VolumeL).ThenBy(t => t.PnBar)
            .FirstOrDefault();
        if (tank is null)
        {
            issues.Add(new Issue("jockey.tankNone", IssueLevel.Warning,
                string.Create(Ru, $"В каталоге нет мембранного бака не менее {minVolume:0} л на давление {systemPressure.Mpa * 10:0.#} бар — подобрать вручную (п. 6.9.4)."),
                Clause: "6.9.4"));
        }

        Pressure? precharge = null;
        double? share = null;
        if (stop is { } p)
        {
            share = (shareMin + shareMax) / 2;
            precharge = new Pressure(((1 - share.Value) * (p.Mpa + Atmosphere)) - Atmosphere);
            if (start is { } s && precharge.Value.Mpa >= s.Mpa)
            {
                issues.Add(new Issue("jockey.tankPrecharge", IssueLevel.Warning,
                    string.Create(Ru, $"Давление воздуха в баке {precharge.Value.Mpa:0.00} МПа не ниже давления включения жокей-насоса {s.Mpa:0.00} МПа — бак опорожнится раньше пуска; уменьшить разность уставок."),
                    Clause: "6.9.4"));
            }
        }

        return new JockeyTankResult(tank, precharge, share, [rule.Id], issues);
    }
}
