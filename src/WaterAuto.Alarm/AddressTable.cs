using WaterAuto.Core;

namespace WaterAuto.Alarm;

public sealed record AddressTableResult(IReadOnlyList<AddressRow> Rows, int Modules);

/// <summary>
/// Таблица адресов (ADR 0012, п. 2): точки управления раскладываются по модулям входов подряд, по одному входу на сигнал,
/// в порядке перечня сигналов. Один контроллер; номера модулей — порядковые. Адреса на линии назначает проектировщик АПС.
/// </summary>
public static class AddressTable
{
    public static AddressTableResult Build(IReadOnlyList<ControlPoint> points, LoopModule module, string controllerName)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(module);
        var controller = $"{controllerName} №1";
        var rows = points.Select((p, i) => new AddressRow(
            controller,
            $"{module.Name} №{(i / module.Inputs) + 1}",
            (i % module.Inputs) + 1,
            p.Designation,
            p.Equipment,
            p.Kind.Text())).ToList();
        return new(rows, points.Count == 0 ? 0 : ((points.Count - 1) / module.Inputs) + 1);
    }
}
