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
    /// <param name="detectors">Число извещателей по ключам каталога набора (smoke, heat, manual); null — без извещателей.</param>
    /// <param name="isolators">Изоляторов КЗ на линиях (ток без адреса).</param>
    /// <param name="powered">Устройства с питанием от РИП (оповещатели, релейные блоки) и их число.</param>
    public static PowerBudgetResult Build(int inputs, AlarmPowerSet set, INormsProvider norms, IReadOnlyDictionary<string, int>? detectors = null, int isolators = 0, IReadOnlyList<(PoweredDevice Device, int Count)>? powered = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        detectors ??= new Dictionary<string, int>();
        powered ??= [];
        var issues = new List<Issue>();
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var c = set.Controller;
        var m = set.Module;
        if (m.Inputs <= 0 || m.LoopCurrent.Amperes < 0 || c.LoadOfLoaded.Amperes <= 0 || c.MaxLoopLoad.Amperes <= 0 || c.MaxAddresses <= 0 || m.MaxPerLoop <= 0)
        {
            // Набор не из каталога (схема такие значения не пропускает): считать нечего, а делить на ноль нельзя.
            issues.Add(new Issue("aps.power.badSet", IssueLevel.Error,
                $"Набор приборов ({c.Name}, {m.Name}) задан неполно: входы модуля, ток и нагрузка линии, число адресов должны быть больше нуля."));
            return new(new PowerBudget(set.Voltage, [], Current.Zero, Current.Zero, set.Supply.OwnCurrent, set.Battery, null, null, null, null), issues);
        }

        var deviceLines = new List<PowerLine>();
        var devices = new List<(int Count, Current Current, int Addresses)>();
        foreach (var (key, count) in detectors.Where(kv => kv.Value > 0))
        {
            if (!set.Detectors.TryGetValue(key, out var device))
            {
                issues.Add(new Issue("aps.power.noDetector", IssueLevel.Error,
                    $"В каталоге приборов питания нет извещателя с ключом «{key}» — {count} ИП в нагрузке линии не учтены."));
                continue;
            }

            devices.Add((count, device.LoopCurrent, device.Addresses));
            deviceLines.Add(new PowerLine(device.Name, count, null, null, $"питание от линии {c.Name}, входит в его ток; {device.Addresses} адрес", device.Source));
        }

        var poweredStandby = 0.0;
        var poweredAlarm = 0.0;
        foreach (var (device, count) in powered.Where(x => x.Count > 0))
        {
            devices.Add((count, device.LoopCurrent, device.Addresses));
            poweredStandby += count * device.Standby.Amperes;
            poweredAlarm += count * device.Alarm.Amperes;
            deviceLines.Add(new PowerLine(device.Name, count, new Current(count * device.Standby.Amperes), new Current(count * device.Alarm.Amperes),
                $"адресное устройство ({device.Addresses} адр.) с питанием от РИП: дежурный и «Пожар» — всего на {count} шт.", device.Source));
        }

        if (isolators > 0)
        {
            if (set.Isolator is { } iso)
            {
                devices.Add((isolators, iso.LoopCurrent, iso.Addresses));
                deviceLines.Add(new PowerLine(iso.Name, isolators, null, null, $"изолятор КЗ, питание от линии {c.Name}; адреса не занимает", iso.Source));
            }
            else
            {
                issues.Add(new Issue("aps.power.noIsolator", IssueLevel.Warning, $"Нужно {isolators} изоляторов КЗ (п. 6.3.5 СП 484), а в каталоге приборов питания изолятора нет — их ток и состав не учтены."));
            }
        }

        var layout = LoopLayout.Plan(inputs, c, m, devices);
        var modules = layout.Modules;
        var controllers = Math.Max(1, layout.Controllers);

        // Сертификаты соответствия, действующие на объекте (А-12): без них изделие в проект не попадает — предупреждение одно на набор.
        var uncertified = new ICertified[] { c, m, set.Supply, set.Battery }.Concat(set.Detectors.Where(kv => detectors.GetValueOrDefault(kv.Key) > 0).Select(kv => (ICertified)kv.Value))
            .Concat(powered.Where(x => x.Count > 0).Select(x => (ICertified)x.Device))
            .Concat(isolators > 0 && set.Isolator is not null ? [set.Isolator] : [])
            .Where(d => string.IsNullOrWhiteSpace(d.Certificate)).Select(d => d.Name).Distinct().ToList();
        if (uncertified.Count > 0)
        {
            issues.Add(new Issue("aps.power.noCertificate", IssueLevel.Warning,
                $"В каталоге не указан сертификат соответствия для: {string.Join(", ", uncertified)}. Для применения в КР нужен действующий сертификат (вопрос А-12) — проверить и внести в alarm_power.json."));
        }

        issues.Add(new Issue("aps.power.defaultSet", IssueLevel.Info,
            $"Расчёт АКБ — для набора из каталога ({c.Name}, {m.Name}, {set.Supply.Name}, {set.Battery.Name}): контроллеры с адресными устройствами, модули сигналов АУПТ, оповещатели и релейные блоки с питанием от РИП; пульт (ППКП) и блок речевого оповещения со своими АКБ в расчёт не входят."));
        if (controllers > 1)
        {
            issues.Add(new Issue("aps.power.controllers", IssueLevel.Info,
                string.Create(ru, $"Принято {controllers} контроллера {c.Name}: на одну линию по паспорту не больше {layout.ModulesPerLine} модулей {m.Name}, {c.MaxAddresses} адресов и {c.MaxLoopLoad.Amperes * 1000:0.#} мА; модули по линиям: {string.Join(", ", layout.ModulesByController)}, извещатели: {string.Join(", ", layout.DevicesByController)}.")));
        }

        // Ток каждого контроллера в дежурном режиме — линейно между двумя точками паспорта по нагрузке его линии
        // (адресов у модуля столько, сколько входов использовано — паспорт АР8, п. 2.2.5.3); сумма — по всем контроллерам.
        var loads = layout.Controllers == 0 ? [0.0] : layout.LoadByController.Select(l => l.Amperes).ToList();
        var standby = new Current(loads.Sum(load => c.Idle.Amperes + ((c.Loaded.Amperes - c.Idle.Amperes) * load / c.LoadOfLoaded.Amperes)) + poweredStandby);
        var alarm = new Current((c.Max.Amperes * controllers) + poweredAlarm);
        issues.Add(new Issue("aps.power.controllerStandby", IssueLevel.Info,
            string.Create(ru, $"Ток {c.Name} в дежурном режиме — по нагрузке линии ({string.Join("; ", loads.Select(l => (l * 1000).ToString("0.#", ru)))} мА) линейно между двумя точками паспорта (допущение программы).")));
        issues.Add(new Issue("aps.power.controllerAlarm", IssueLevel.Info,
            $"Отдельного тока режима «Пожар» у {c.Name} в паспорте нет — принят максимальный (в запас; вопрос А-10)."));

        List<PowerLine> lines =
        [
            new(c.Name, controllers, new Current(standby.Amperes - poweredStandby), new Current(alarm.Amperes - poweredAlarm), "всего по контроллерам: дежурный — по нагрузке линии, «Пожар» — максимальный", c.Source),
            new(m.Name, modules, null, null, $"питание от линии {c.Name}, входит в его ток", m.Source),
            .. deviceLines,
        ];

        // Число РИП — по току нагрузки в режиме «Пожар» против паспортного; нагрузка делится между источниками поровну
        // (у каждого своя АКБ), а какие приборы на какой источник — решает проектировщик.
        var supplies = 1;
        if (set.Supply.MaxOutput is { } maxOut && alarm.Amperes > maxOut.Amperes + 1e-12)
        {
            supplies = (int)Math.Ceiling(alarm.Amperes / maxOut.Amperes);
            issues.Add(new Issue("aps.power.supplyOverload", IssueLevel.Warning,
                string.Create(ru, $"Ток в режиме «Пожар» {alarm.Amperes:0.##} А больше номинального тока нагрузки {set.Supply.Name} ({maxOut.Amperes:0.##} А по паспорту): нужно не менее {supplies} источников; принято {supplies}, нагрузка поделена поровну, ёмкость АКБ рассчитана на один источник; распределение приборов по источникам — проектировщик.")));
        }

        lines.Add(new(set.Supply.Name, supplies, set.Supply.OwnCurrent, set.Supply.OwnCurrent, supplies > 1 ? "собственный ток от АКБ (Iибэ) каждого источника" : "собственный ток от АКБ (Iибэ)", set.Supply.Source));

        var own = set.Supply.OwnCurrent;
        var standbyPer = new Current(standby.Amperes / supplies);
        var alarmPer = new Current(alarm.Amperes / supplies);
        var hourIssues = new List<Issue>();
        var td = BatteryCapacity.Hours(norms, BatteryCapacity.StandbyTimeId, hourIssues);
        var tp = BatteryCapacity.Hours(norms, BatteryCapacity.AlarmTimeId, hourIssues);
        issues.AddRange(hourIssues);
        var kd = td is { } hd ? LoadFactor(set.Battery, hd, standbyPer.Amperes + own.Amperes, "дежурного режима", issues) : null;
        var kp = tp is { } hp ? LoadFactor(set.Battery, hp, alarmPer.Amperes + own.Amperes, "режима «Пожар»", issues) : null;

        var aging = BatteryCapacity.Aging(set.Battery.EndOfLifePercent, []);
        Charge? required = null;
        TimeSpan? backup = null;
        if (kd is { } kStandby && kp is { } kAlarm)
        {
            var capacity = BatteryCapacity.Required(new BatteryLoad(standbyPer, alarmPer, own), new BatteryFactors(kStandby, kAlarm, set.Battery.EndOfLifePercent), norms);
            issues.AddRange(capacity.Issues);
            required = capacity.Required;
            if (required is { } r && r.AmpereHours > set.Battery.Nominal.AmpereHours)
            {
                issues.Add(new Issue("aps.power.capacity", IssueLevel.Warning,
                    string.Create(ru, $"Требуется АКБ не менее {r.AmpereHours:0.##} А·ч{(supplies > 1 ? $" на каждый из {supplies} источников" : "")}, {set.Battery.Name} — {set.Battery.Nominal.AmpereHours:0.##} А·ч: нужна АКБ большей ёмкости или дополнительный бокс."),
                    Clause: Doc));
            }

            var time = BatteryCapacity.Backup(set.Battery.Nominal, alarmPer, set.Battery.EndOfLifePercent);
            issues.AddRange(time.Issues);
            backup = time.Time;
        }

        return new(new PowerBudget(set.Voltage, lines, standby, alarm, own, set.Battery, kd, kp, required, backup, aging, supplies), issues);
    }

    /// <summary>
    /// Kн = C20 / (I·t) по паспорту АКБ: I — ток разряда за время t из таблицы разряда, t — время работы по норме.
    /// Ток нагрузки (с Iибэ — АКБ питает и сам РИП) не должен превышать I, иначе нужна АКБ большей ёмкости.
    /// </summary>
    private static double? LoadFactor(BatteryModel battery, double hours, double load, string mode, List<Issue> issues)
    {
        if (!battery.Discharge.TryGetValue(TimeSpan.FromHours(hours), out var current) || current.Amperes <= 0)
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
