using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>Раскладка модулей входов по линиям контроллеров: число контроллеров и модулей на каждой линии (подряд).</summary>
public sealed record LoopLayoutResult(int Modules, int ModulesPerLine, IReadOnlyList<int> ModulesByController)
{
    public int Controllers => ModulesByController.Count;
}

/// <summary>
/// Общая для таблицы адресов и расчёта АКБ раскладка (ADR 0012, этап 1): модули заполняются входами подряд, на линию —
/// не больше, чем позволяют паспорт модуля (штук на линию), число адресов и допустимая нагрузка линии контроллера.
/// Когда модули не помещаются на одну линию, добавляется следующий контроллер.
/// </summary>
public static class LoopLayout
{
    public static LoopLayoutResult Plan(int inputs, LoopController controller, LoopModule module)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(module.Inputs, 0, nameof(module));
        var modules = inputs <= 0 ? 0 : (inputs + module.Inputs - 1) / module.Inputs;
        // Предел по току — в double: при очень малом токе модуля частное не помещается в int.
        var byLoad = module.LoopCurrent.Amperes > 0 ? Math.Floor(controller.MaxLoopLoad.Amperes / module.LoopCurrent.Amperes) : double.PositiveInfinity;
        var perLine = Math.Max(1, (int)Math.Min(Math.Min(module.MaxPerLoop, controller.MaxAddresses / module.Inputs), Math.Min(byLoad, int.MaxValue)));
        var byController = new List<int>();
        for (var left = modules; left > 0; left -= perLine)
        {
            byController.Add(Math.Min(perLine, left));
        }

        return new(modules, perLine, byController);
    }
}
