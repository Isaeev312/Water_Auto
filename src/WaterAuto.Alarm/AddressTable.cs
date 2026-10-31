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
    /// <summary>Адресное устройство для таблицы: обозначение, оборудование, сигнал, ЗКСПС и ток (для раскладки по линиям).</summary>
    public sealed record AddressDevice(string Designation, string Equipment, string Signal, string Zone, Current Current);

    public static AddressTableResult Build(IReadOnlyList<ControlPoint> points, LoopController controller, LoopModule module) => Build(points, controller, module, []);

    public static AddressTableResult Build(IReadOnlyList<ControlPoint> points, LoopController controller, LoopModule module, IReadOnlyList<AddressDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(devices);
        var layout = LoopLayout.Plan(points.Count, controller, module, devices.GroupBy(d => d.Current).Select(g => (g.Count(), g.Key, 1)).ToList());
        var rows = points.Select((p, i) => new AddressRow(
            $"{controller.Name} №{(i / module.Inputs / layout.ModulesPerLine) + 1}",
            $"{module.Name} №{(i / module.Inputs) + 1}",
            (i % module.Inputs) + 1,
            p.Designation,
            p.Equipment,
            p.Kind.Text())).ToList();

        // Извещатели — по линиям так же, как в расчёте питания (поровну), в порядке раскладки; вход 0 — своё адресное устройство.
        var offset = 0;
        for (var k = 0; k < layout.DevicesByController.Count; k++)
        {
            foreach (var d in devices.Skip(offset).Take(layout.DevicesByController[k]))
            {
                rows.Add(new AddressRow($"{controller.Name} №{k + 1}", "—", 0, d.Designation, d.Equipment, d.Signal, d.Zone));
            }

            offset += layout.DevicesByController[k];
        }

        return new(rows, layout.Modules);
    }
}
