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
        var modules = inputs <= 0 ? 0 : (inputs + module.Inputs - 1) / module.Inputs;
        var byLoad = module.LoopCurrent.Amperes > 0 ? (int)Math.Floor(controller.MaxLoopLoad.Amperes / module.LoopCurrent.Amperes) : int.MaxValue;
        var perLine = Math.Max(1, Math.Min(module.MaxPerLoop, Math.Min(controller.MaxAddresses / module.Inputs, byLoad)));
        var byController = new List<int>();
        for (var left = modules; left > 0; left -= perLine)
        {
            byController.Add(Math.Min(perLine, left));
        }

        return new(modules, perLine, byController);
    }
}
