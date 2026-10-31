using WaterAuto.Core;

namespace WaterAuto.Alarm;

public sealed record AddressTableResult(IReadOnlyList<AddressRow> Rows, int Modules);

/// <summary>
/// Таблица адресов (ADR 0012, п. 2): точки управления раскладываются по модулям входов подряд, по одному входу на сигнал,
/// в порядке перечня сигналов. Контроллеры — по <see cref="LoopLayout"/>; номера модулей — сквозные. Адреса на линии
/// назначает проектировщик АПС.
/// </summary>
public static class AddressTable
{
    public static AddressTableResult Build(IReadOnlyList<ControlPoint> points, LoopController controller, LoopModule module)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(module);
        var layout = LoopLayout.Plan(points.Count, controller, module);
        var rows = points.Select((p, i) => new AddressRow(
            $"{controller.Name} №{(i / module.Inputs / layout.ModulesPerLine) + 1}",
            $"{module.Name} №{(i / module.Inputs) + 1}",
            (i % module.Inputs) + 1,
            p.Designation,
            p.Equipment,
            p.Kind.Text())).ToList();
        return new(rows, layout.Modules);
    }
}
