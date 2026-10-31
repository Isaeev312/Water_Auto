using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>Токи СПЗ от АКБ: ΣIд.р. — дежурный режим, ΣIр.п. — режим «Пожар», Iибэ — собственный ток ИБЭ (паспорт ИБЭ).</summary>
public sealed record BatteryLoad(Current Standby, Current Alarm, Current SupplyOwn);

/// <summary>
/// Коэффициенты из паспорта АКБ: Kн для дежурного режима и для «Пожара» (одно значение — оба равны)
/// и ёмкость в конце срока службы S, % от начальной.
/// </summary>
public sealed record BatteryFactors(double StandbyLoadFactor, double AlarmLoadFactor, double EndOfLifePercent);

public sealed record BatteryCapacityResult(Charge? Required, IReadOnlyList<Issue> Issues);

public sealed record BatteryBackupResult(TimeSpan? Time, IReadOnlyList<Issue> Issues);

/// <summary>Ёмкость АКБ и время работы от неё — прил. Б СП 6.13130.2025 (open_questions.md, А-03).</summary>
public static class BatteryCapacity
{
    public const string StandbyTimeId = "sp6.b2.standbyTime";
    public const string AlarmTimeId = "sp6.b2.alarmTime";

    private const string Doc = "СП 6.13130.2025, прил. Б";

    /// <summary>
    /// C = Kстр·(Kн.д·(ΣIд + Iибэ)·tд + Kн.п·(ΣIп + Iибэ)·tп) — формула Б.1.1 с током «Пожара» во втором слагаемом
    /// (решение владельца по опечатке, А-03). При Kн.д = Kн.п совпадает с Б.1. Время tд и tп — из данных норм.
    /// </summary>
    public static BatteryCapacityResult Required(BatteryLoad load, BatteryFactors factors, INormsProvider norms)
    {
        var issues = new List<Issue>();
        var standbyHours = Hours(norms, StandbyTimeId, issues);
        var alarmHours = Hours(norms, AlarmTimeId, issues);
        CheckCurrents(issues, load.Standby, load.Alarm, load.SupplyOwn);
        if (factors.StandbyLoadFactor <= 0 || factors.AlarmLoadFactor <= 0)
        {
            issues.Add(new Issue("aps.battery.loadFactor", IssueLevel.Error,
                "Коэффициент Kн должен быть больше нуля (паспорт АКБ).", Clause: Doc + ", п. Б.2"));
        }

        var aging = Aging(factors.EndOfLifePercent, issues);
        if (issues.Count > 0 || standbyHours is not { } td || alarmHours is not { } tp || aging is not { } kAging)
        {
            return new(null, issues);
        }

        if (factors.StandbyLoadFactor != factors.AlarmLoadFactor)
        {
            issues.Add(new Issue("aps.battery.b11Correction", IssueLevel.Info,
                "Ёмкость по формуле Б.1.1 с током режима «Пожар» во втором слагаемом (в тексте — ток дежурного режима; решение владельца, А-03).",
                Clause: Doc + ", п. Б.3"));
        }

        var own = load.SupplyOwn.Amperes;
        var ampereHours = kAging * (factors.StandbyLoadFactor * (load.Standby.Amperes + own) * td
            + factors.AlarmLoadFactor * (load.Alarm.Amperes + own) * tp);
        return new(new Charge(ampereHours), issues);
    }

    /// <summary>t = C / (ΣIр.п.·Kстр) — формула Б.3; ток ИБЭ в ней не участвует, как в тексте нормы.</summary>
    public static BatteryBackupResult Backup(Charge capacity, Current alarm, double endOfLifePercent)
    {
        var issues = new List<Issue>();
        if (alarm.Amperes <= 0)
        {
            issues.Add(new Issue("aps.battery.current", IssueLevel.Error,
                "Для времени работы нужен ток режима «Пожар» больше нуля.", Clause: Doc + ", п. Б.5"));
        }

        var aging = Aging(endOfLifePercent, issues);
        if (issues.Count > 0 || aging is not { } kAging)
        {
            return new(null, issues);
        }

        return new(TimeSpan.FromHours(capacity.AmpereHours / (alarm.Amperes * kAging)), issues);
    }

    /// <summary>Kстр = 100 % / S — формула Б.2; 100 % — начальная ёмкость АКБ по определению, не норматив.</summary>
    private static double? Aging(double endOfLifePercent, List<Issue> issues)
    {
        if (endOfLifePercent is > 0 and <= 100)
        {
            return 100.0 / endOfLifePercent;
        }

        issues.Add(new Issue("aps.battery.endOfLife", IssueLevel.Error,
            $"Ёмкость АКБ в конце срока службы S = {endOfLifePercent} % — нужна доля от начальной ёмкости больше 0 и не больше 100 % (паспорт АКБ).",
            Clause: Doc + ", п. Б.4"));
        return null;
    }

    private static void CheckCurrents(List<Issue> issues, params Current[] currents)
    {
        if (currents.Any(c => c.Amperes < 0))
        {
            issues.Add(new Issue("aps.battery.current", IssueLevel.Error,
                "Ток потребления не может быть отрицательным.", Clause: Doc + ", п. Б.2"));
        }
    }

    internal static double? Hours(INormsProvider norms, string id, List<Issue> issues)
    {
        if (norms.Find(id)?.Values.GetValueOrDefault("t") is { } hours)
        {
            return hours;
        }

        issues.Add(new Issue("aps.battery.noNorm", IssueLevel.Error,
            $"В данных норм нет записи {id} (время работы от АКБ).", Clause: Doc + ", п. Б.2"));
        return null;
    }
}
