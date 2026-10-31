using WaterAuto.Alarm;
using WaterAuto.Core;

namespace WaterAuto.Alarm.Tests;

// Таблица адресов (ADR 0012, п. 2): точки управления раскладываются по модулям входов подряд, по одному входу на сигнал.
public class AddressTableTests
{
    private static readonly LoopModule Module = new("С2000-АР8", 8, new Current(0.004), 15, "РЭ");

    private static ControlPoint Point(string id, string mark, SignalKind kind = SignalKind.ValvePosition) =>
        new(id, "Оборудование", kind, null, id.Split('.')[0], mark, null);

    [Fact]
    public void Points_fill_module_inputs_in_order_and_overflow_to_the_next_module()
    {
        var points = Enumerable.Range(1, 10).Select(n => Point($"cuValve.{n}", $"ЗД{n}")).ToList();

        var table = AddressTable.Build(points, Module, "С2000-КДЛ");

        table.Rows.Count.ShouldBe(10);
        table.Rows[0].ShouldBe(new AddressRow("С2000-КДЛ №1", "С2000-АР8 №1", 1, "ЗД1", "Оборудование", "Положение запорного устройства"));
        table.Rows[7].Input.ShouldBe(8);
        table.Rows[8].Module.ShouldBe("С2000-АР8 №2");
        table.Rows[8].Input.ShouldBe(1);
        table.Modules.ShouldBe(2);
    }

    [Fact]
    public void Signal_text_follows_the_kind()
    {
        var table = AddressTable.Build([Point("cabinet.pumpFault.1", "ШУН", SignalKind.PumpFault)], Module, "С2000-КДЛ");

        table.Rows.Single().Signal.ShouldBe("Неисправность (обобщённый выход)");
    }

    [Fact]
    public void Empty_list_gives_empty_table()
    {
        var table = AddressTable.Build([], Module, "С2000-КДЛ");

        table.Rows.ShouldBeEmpty();
        table.Modules.ShouldBe(0);
    }
}
