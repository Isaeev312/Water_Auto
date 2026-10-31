using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>Раскладка модулей входов по линиям контроллеров: число контроллеров и модулей на каждой линии (подряд).</summary>
public sealed record LoopLayoutResult(int Modules, int ModulesPerLine, IReadOnlyList<int> ModulesByController, IReadOnlyList<int> DevicesByController, IReadOnlyList<Current> LoadByController)
{
    public int Controllers => ModulesByController.Count;
}

/// <summary>
/// Общая для таблицы адресов и расчёта АКБ раскладка (ADR 0012): модули заполняются входами подряд, на линию —
/// не больше, чем позволяют паспорт модуля (штук на линию), число адресов и допустимая нагрузка линии контроллера;
/// извещатели этапа 2 добавляют адреса и ток. Когда линия переполнена, добавляется следующий контроллер.
/// </summary>
public static class LoopLayout
{
    public static LoopLayoutResult Plan(int inputs, LoopController controller, LoopModule module) => Plan(inputs, controller, module, []);

    /// <param name="devices">Прочие адресные устройства линии (извещатели): количество и ток каждого; по одному адресу.</param>
    public static LoopLayoutResult Plan(int inputs, LoopController controller, LoopModule module, IReadOnlyList<(int Count, Current Current, int Addresses)> devices)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(module.Inputs, 0, nameof(module));
        var modules = inputs <= 0 ? 0 : (inputs + module.Inputs - 1) / module.Inputs;
        // Предел по току — в double: при очень малом токе модуля частное не помещается в int.
        var byLoad = module.LoopCurrent.Amperes > 0 ? Math.Floor(controller.MaxLoopLoad.Amperes / module.LoopCurrent.Amperes) : double.PositiveInfinity;
        var perLine = Math.Max(1, (int)Math.Min(Math.Min(module.MaxPerLoop, controller.MaxAddresses / module.Inputs), Math.Min(byLoad, int.MaxValue)));

        // Число контроллеров — по самому жёсткому из пределов: модули на линию, адреса (входы + устройства), ток линии.
        var deviceCount = devices.Sum(d => Math.Max(0, d.Count));
        var deviceAddresses = devices.Sum(d => Math.Max(0, d.Count) * Math.Max(0, d.Addresses));
        var deviceLoad = devices.Sum(d => Math.Max(0, d.Count) * d.Current.Amperes);
        var totalLoad = (modules * module.LoopCurrent.Amperes) + deviceLoad;
        var controllers = Math.Max(
            (modules + perLine - 1) / perLine,
            Math.Max(
                (int)Math.Ceiling((double)(inputs + deviceAddresses) / controller.MaxAddresses),
                controller.MaxLoopLoad.Amperes > 0 ? (int)Math.Ceiling(totalLoad / controller.MaxLoopLoad.Amperes - 1e-9) : 0));
        if (controllers == 0)
        {
            return new(0, perLine, [], [], []);
        }

        var modulesBy = new List<int>();
        for (var i = 0; i < controllers; i++)
        {
            var left = modules - modulesBy.Sum();
            modulesBy.Add(Math.Min(perLine, Math.Max(0, left)));
        }

        // Извещатели — поровну по линиям; средний ток устройства — для нагрузки линии.
        var avg = deviceCount > 0 ? deviceLoad / deviceCount : 0;
        var devicesBy = Enumerable.Range(0, controllers).Select(i => (deviceCount / controllers) + (i < deviceCount % controllers ? 1 : 0)).ToList();
        var loadBy = modulesBy.Zip(devicesBy, (mod, dev) => new Current((mod * module.LoopCurrent.Amperes) + (dev * avg))).ToList();
        return new(modules, perLine, modulesBy, devicesBy, loadBy);
    }
}
