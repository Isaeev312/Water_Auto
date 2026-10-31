using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Alarm;

public sealed record PowerBudgetResult(PowerBudget Budget, IReadOnlyList<Issue> Issues);

/// <summary>
/// Питание приборов автоматики АУПТ в АПС (ADR 0012, этап 1): контроллер линии, модули входов по числу сигналов, РИП с АКБ.
/// Токи — из паспортов (каталог), ёмкость — по прил. Б СП 6.13130.2025 (<see cref="BatteryCapacity"/>).
/// </summary>
public static class PowerBudgets
{
    private const string Doc = "СП 6.13130.2025, прил. Б";

    /// <param name="inputs">Число входов модулей — по одному на точку управления.</param>
    public static PowerBudgetResult Build(int inputs, AlarmPowerSet set, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(set);
        var issues = new List<Issue>();
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var c = set.Controller;
        var m = set.Module;
        var modules = inputs <= 0 ? 0 : (inputs + m.Inputs - 1) / m.Inputs;
        var loopLoad = modules * m.LoopCurrent.Amperes;

        issues.Add(new Issue("aps.power.defaultSet", IssueLevel.Info,
            $"Расчёт АКБ — для набора из каталога ({c.Name}, {m.Name}, {set.Supply.Name}, {set.Battery.Name}), только приборы автоматики АУПТ; пульт и приборы АПС, СОУЭ (этапы 2–3) не учтены."));
        if (modules > m.MaxPerLoop)
        {
            issues.Add(new Issue("aps.power.loopModules", IssueLevel.Warning,
                $"{m.Name}: {modules} шт. — больше {m.MaxPerLoop} на одну линию {c.Name} по паспорту; нужен второй контроллер."));
        }

        if (loopLoad > c.MaxLoopLoad.Amperes || modules * m.Inputs > c.MaxAddresses)
        {
            issues.Add(new Issue("aps.power.loopLoad", IssueLevel.Warning,
                string.Create(ru, $"Нагрузка линии {c.Name} {loopLoad * 1000:0.#} мА, адресов {modules * m.Inputs} — больше паспортных {c.MaxLoopLoad.Amperes * 1000:0.#} мА или {c.MaxAddresses}.")));
        }

        // Ток контроллера в дежурном режиме — линейно между двумя точками паспорта (без нагрузки и с известной нагрузкой линии).
        var standby = new Current(c.Idle.Amperes + ((c.Loaded.Amperes - c.Idle.Amperes) * loopLoad / c.LoadOfLoaded.Amperes));
        var alarm = c.Max;
        issues.Add(new Issue("aps.power.controllerStandby", IssueLevel.Info,
            string.Create(ru, $"Ток {c.Name} в дежурном режиме — по нагрузке линии {loopLoad * 1000:0.#} мА линейно между двумя точками паспорта (допущение программы).")));
        issues.Add(new Issue("aps.power.controllerAlarm", IssueLevel.Info,
            $"Отдельного тока режима «Пожар» у {c.Name} в паспорте нет — принят максимальный (в запас; вопрос А-10)."));

        var lines = new List<PowerLine>
        {
            new(c.Name, 1, standby, alarm, "дежурный — по нагрузке линии, «Пожар» — максимальный", c.Source),
            new(m.Name, modules, null, null, $"питание от линии {c.Name}, входит в его ток", m.Source),
            new(set.Supply.Name, 1, set.Supply.OwnCurrent, set.Supply.OwnCurrent, "собственный ток от АКБ (Iибэ)", set.Supply.Source),
        };

        var own = set.Supply.OwnCurrent;
        var hourIssues = new List<Issue>();
        var td = BatteryCapacity.Hours(norms, BatteryCapacity.StandbyTimeId, hourIssues);
        var tp = BatteryCapacity.Hours(norms, BatteryCapacity.AlarmTimeId, hourIssues);
        issues.AddRange(hourIssues);
        var kd = td is { } hd ? LoadFactor(set.Battery, hd, standby.Amperes + own.Amperes, "дежурного режима", issues) : null;
        var kp = tp is { } hp ? LoadFactor(set.Battery, hp, alarm.Amperes + own.Amperes, "режима «Пожар»", issues) : null;

        Charge? required = null;
        TimeSpan? backup = null;
        if (kd is { } kStandby && kp is { } kAlarm)
        {
            var capacity = BatteryCapacity.Required(new BatteryLoad(standby, alarm, own), new BatteryFactors(kStandby, kAlarm, set.Battery.EndOfLifePercent), norms);
            issues.AddRange(capacity.Issues);
            required = capacity.Required;
            if (required is { } r && r.AmpereHours > set.Battery.Nominal.AmpereHours)
            {
                issues.Add(new Issue("aps.power.capacity", IssueLevel.Warning,
                    string.Create(ru, $"Требуется АКБ не менее {r.AmpereHours:0.##} А·ч, {set.Battery.Name} — {set.Battery.Nominal.AmpereHours:0.##} А·ч: нужна АКБ большей ёмкости или дополнительный бокс."),
                    Clause: Doc));
            }

            var time = BatteryCapacity.Backup(set.Battery.Nominal, alarm, set.Battery.EndOfLifePercent);
            issues.AddRange(time.Issues);
            backup = time.Time;
        }

        return new(new PowerBudget(set.Voltage, lines, standby, alarm, own, set.Battery, kd, kp, required, backup), issues);
    }

    /// <summary>
    /// Kн = C20 / (I·t) по паспорту АКБ: I — ток разряда за время t из таблицы разряда, t — время работы по норме.
    /// Ток нагрузки (с Iибэ — АКБ питает и сам РИП) не должен превышать I, иначе нужна АКБ большей ёмкости.
    /// </summary>
    private static double? LoadFactor(BatteryModel battery, double hours, double load, string mode, List<Issue> issues)
    {
        if (!battery.Discharge.TryGetValue(hours, out var current) || current.Amperes <= 0)
        {
            issues.Add(new Issue("aps.power.discharge", IssueLevel.Error,
                string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"В каталоге нет тока разряда {battery.Name} за {hours:0.##} ч — Kн {mode} не определить."),
                Clause: Doc));
            return null;
        }

        if (load > current.Amperes)
        {
            issues.Add(new Issue("aps.power.dischargeLimit", IssueLevel.Warning,
                string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"Ток {mode} с Iибэ {load:0.###} А больше тока разряда {battery.Name} за {hours:0.##} ч ({current.Amperes:0.###} А): по паспорту АКБ нужна АКБ большей ёмкости.")));
        }

        return battery.Nominal.AmpereHours / (current.Amperes * hours);
    }
}
