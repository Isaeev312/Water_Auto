using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Powder;

/// <summary>
/// Цепи пуска модулей порошкового пожаротушения от блока пуска АУП: модули на выходе включаются параллельно, их число —
/// по току выхода и паспортному току пуска модуля; выходы — по помещениям; блоки — по числу выходов. Ток контроля цепей
/// блока должен быть не больше безопасного тока проверки модуля (иначе ложный пуск). Числа — паспорта блока и модуля.
/// </summary>
public static class PowderStart
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static PowderStartPlan Plan(IReadOnlyList<(string RoomId, int Modules)> rooms, double? startCurrent, double? moduleSafeCurrent, PowderStartUnit unit)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(unit);
        var issues = new List<Issue>();
        if (startCurrent is not { } i0 || i0 <= 0)
        {
            issues.Add(new Issue("powder.start.noCurrent", IssueLevel.Error, $"Ток пуска модуля в каталоге не указан — цепи пуска от {unit.Name} не рассчитаны."));
            return new(0, new Dictionary<string, int>(), 0, issues);
        }

        if (moduleSafeCurrent is { } safe && unit.MonitorCurrent > safe + 1e-12)
        {
            issues.Add(new Issue("powder.start.monitor", IssueLevel.Error,
                string.Create(Ru, $"Ток контроля цепей {unit.Name} {unit.MonitorCurrent * 1000:0.#} мА больше безопасного тока проверки модуля {safe * 1000:0.#} мА — возможен ложный пуск; нужен другой блок пуска.")));
        }
        else if (moduleSafeCurrent is null)
        {
            issues.Add(new Issue("powder.start.noSafe", IssueLevel.Warning, $"Безопасный ток проверки цепи пуска модуля в каталоге не указан — совместимость с током контроля {unit.Name} не проверена."));
        }

        var perOutput = Math.Max(1, (int)Math.Floor((unit.MaxOutputCurrent / i0) + 1e-9));
        var outputs = rooms.ToDictionary(r => r.RoomId, r => (r.Modules + perOutput - 1) / perOutput, StringComparer.Ordinal);
        foreach (var (roomId, modules) in rooms.Where(r => r.Modules * i0 > unit.MaxTotalCurrent + 1e-9))
        {
            issues.Add(new Issue("powder.start.total", IssueLevel.Warning,
                string.Create(Ru, $"Ток пуска всех модулей помещения {modules} × {i0:0.##} А = {modules * i0:0.#} А больше суммарного тока {unit.Name} {unit.MaxTotalCurrent:0.#} А — выходы включаются последовательно с пусковым интервалом (все — не дольше 3 с, п. 10.2.4 СП 485) или помещение делится между блоками."),
                roomId));
        }

        var units = (outputs.Values.Sum() + unit.Outputs - 1) / unit.Outputs;
        return new(perOutput, outputs, units, issues);
    }
}
