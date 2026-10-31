using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Alarm;

public sealed record HvacCommandsResult(IReadOnlyList<HvacCommand> Commands, int Outputs, IReadOnlyList<Issue> Issues);

/// <summary>
/// Команды СПС в системы вентиляции при пожаре (ADR 0012, этап 3, АППЗ): по СП 484 разд. 7 и СП 7.13130. Состав систем и
/// дымовых зон — от раздела ОВ (исходные данные проекта); программа формирует перечень команд, их триггеры и задержки,
/// а число выходов (реле) — для спецификации и расчёта питания. Алгоритм совместной работы систем — раздел ОВ (СП 7, п. 7.20).
/// </summary>
public static class HvacCommands
{
    public const string ShutdownId = "sp7.p6_24.shutdown";
    public const string DelayId = "sp7.p7_20.supplyDelay";
    public const string SmokeStartId = "sp484.p7_7_1.smokeControlStart";
    public const string GeneralId = "sp484.p7_7_4.generalVentilation";

    public static HvacCommandsResult Build(HvacInputs inputs, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(norms);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var issues = new List<Issue>();
        var records = new[] { ShutdownId, DelayId, SmokeStartId, GeneralId }.ToDictionary(id => id, norms.Find);
        foreach (var (id, r) in records.Where(kv => kv.Value is null))
        {
            issues.Add(new Issue("aps.hvac.noNorm", IssueLevel.Error, $"В данных норм нет записи {id} (управление вентиляцией при пожаре)."));
        }

        if (records.Values.Any(r => r is null))
        {
            return new([], 0, issues);
        }

        var lead = records[DelayId]!.Values["exhaustLeadMin"]!.Value;
        var commands = new List<HvacCommand>();
        var anyZone = "«Пожар» из любой ЗКСПС или от любого участка АУП";
        if (inputs.GeneralVentilation.Count > 0)
        {
            commands.Add(new("ОВ-1", HvacCommandKind.StopGeneralVentilation, null, inputs.GeneralVentilation, inputs.GeneralVentilation.Count, anyZone, null,
                "СП 7.13130.2013, п. 6.24; СП 484.1311500.2020, п. 7.7.4",
                "частичное отключение — только если определено разделом ОВ, иначе по всему объекту"));
        }

        if (inputs.NormallyOpenDampers > 0)
        {
            commands.Add(new("ОВ-2", HvacCommandKind.CloseNormallyOpenDampers, null, [], inputs.NormallyOpenDampers, anyZone, null,
                "СП 7.13130.2013, пп. 6.24, 7.19", "клапаны сохраняют положение при отключении питания привода"));
        }

        var n = commands.Count;
        foreach (var z in inputs.SmokeZones)
        {
            var trigger = $"«Пожар» из ЗКСПС дымовой зоны {z.Name} или от участка АУП этой зоны (первый поступивший сигнал)";
            if (z.SmokeDampers > 0)
            {
                commands.Add(new($"ОВ-{++n}", HvacCommandKind.OpenSmokeDampers, z.Name, [], z.SmokeDampers, trigger, null,
                    "СП 484.1311500.2020, пп. 7.7.1, 7.7.2, 7.7.6", "клапаны дымоудаления зоны — до пуска вентилятора"));
            }

            commands.Add(new($"ОВ-{++n}", HvacCommandKind.StartSmokeExhaust, z.Name, [z.ExhaustSystem], 1, trigger, null,
                "СП 484.1311500.2020, пп. 7.7.1, 7.7.6; СП 7.13130.2013, п. 7.20", "автоматический и дистанционный пуск"));
            if (z.SupplySystem is { } supply)
            {
                commands.Add(new($"ОВ-{++n}", HvacCommandKind.StartSmokeSupply, z.Name, [supply], 1, $"через {lead:0} с после пуска {z.ExhaustSystem}", lead,
                    "СП 7.13130.2013, п. 7.20", string.Create(ru, $"опережение вытяжной на {records[DelayId]!.Values["exhaustLeadMin"]:0}–{records[DelayId]!.Values["exhaustLeadMax"]:0} с; принято наименьшее")));
            }
        }

        issues.Add(new Issue("aps.hvac.algorithm", IssueLevel.Info,
            "Состав систем вентиляции, дымовых зон и клапанов — по разделу ОВ; алгоритм совместной работы и частичного отключения (СП 7, пп. 6.24, 7.20) задаёт раздел ОВ, здесь — команды и их триггеры для ТЗ."));
        return new(commands, commands.Count, issues);
    }
}
