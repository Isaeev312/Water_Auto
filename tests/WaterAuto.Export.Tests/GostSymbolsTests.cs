using ACadSharp;
using ACadSharp.Entities;
using WaterAuto.Core;
using WaterAuto.Export;

namespace WaterAuto.Export.Tests;

// Условные графические обозначения элементов трубопроводов по ГОСТ 21.205-2016 (табл. 1, 2, 6): форма знака по виду.
public class GostSymbolsTests
{
    private static List<Entity> Draw(PipeSymbol kind)
    {
        var doc = new CadDocument();
        var layer = new ACadSharp.Tables.Layer("S");
        doc.Layers.Add(layer);
        GostSymbols.Draw(doc, layer, layer, new CSMath.XY(0, 0), 0, 10, kind);
        return [.. doc.Entities];
    }

    [Fact]
    public void Valve_symbols_differ_as_in_table_6()
    {
        // Клапан запорный — два треугольника вершинами друг к другу; задвижка — то же с вертикальной чертой через вершины.
        var shutOff = Draw(PipeSymbol.ShutOff);
        var gate = Draw(PipeSymbol.Gate);
        gate.OfType<Line>().ShouldContain(l => Math.Abs(l.StartPoint.X) < 1e-9 && Math.Abs(l.EndPoint.X) < 1e-9);
        shutOff.OfType<Line>().ShouldNotContain(l => Math.Abs(l.StartPoint.X) < 1e-9 && Math.Abs(l.EndPoint.X) < 1e-9);

        // Обратный клапан — залитый треугольник со стороны выхода (движение среды — от белого к чёрному).
        var check = Draw(PipeSymbol.Check);
        var fill = check.OfType<Solid>().Single();
        new[] { fill.FirstCorner.X, fill.SecondCorner.X, fill.ThirdCorner.X }.Average().ShouldBeGreaterThan(0);

        // Кран шаровой — кружок между треугольниками; затвор дисковый — прямоугольник с наклонной чертой.
        Draw(PipeSymbol.Ball).OfType<Circle>().ShouldContain(c => Math.Abs(c.Center.X) < 1e-9);
        Draw(PipeSymbol.Butterfly).OfType<LwPolyline>().ShouldContain(p => p.Vertices.Count == 4 && p.IsClosed);
    }

    [Fact]
    public void Section_draws_valves_by_flow_direction_with_a_legend()
    {
        // Обратный клапан у патрубка ГМ: среда идёт влево (угол π) — залитый треугольник слева от центра знака; легенда на листе.
        var section = new PumpRoomSection(0, 8000, 300, -3, 4.5, 3, null, [new SectionBlock("Установка", 1000, 3000, 0.2, 1.8, Pumps: 2)], [new SectionPipe("DN80", 1000, 8000, 2.4, [])], [])
        {
            Valves = [(6000, 2.4, "check", 9, Math.PI), (5000, 2.4, "gate", 10, Math.PI), (2000, 2.4, "butterfly", 6, 0)],
        };
        var doc = PumpRoomSectionDxfWriter.Build(section, new TitleBlock("1-ПТ", "Объект", "Здание", "Разрез", "Р", "Орг.", []));

        var check = doc.Entities.OfType<Solid>().Where(s => Math.Abs(s.FirstCorner.Y - 2400) < 2000 && s.FirstCorner.X > 5000 && s.FirstCorner.X < 7000).ToList();
        check.ShouldNotBeEmpty();
        check.Average(s => (s.FirstCorner.X + s.SecondCorner.X + s.ThirdCorner.X) / 3).ShouldBeLessThan(6000);
        var texts = doc.Entities.OfType<TextEntity>().Select(t => t.Value).ToList();
        texts.ShouldContain("Условные обозначения");
        texts.ShouldContain(t => t.StartsWith("задвижка", StringComparison.Ordinal));
        texts.ShouldContain(t => t.StartsWith("затвор дисковый", StringComparison.Ordinal));
        texts.ShouldContain(t => t.StartsWith("насос", StringComparison.Ordinal));
    }

    [Fact]
    public void Instruments_pump_and_filter_have_their_own_marks()
    {
        // Манометр по ГОСТ 21.208 — круг с буквами PI на отводе; насос — круг с залитым треугольником нагнетания; фильтр — ромб.
        Draw(PipeSymbol.Gauge).OfType<TextEntity>().ShouldContain(t => t.Value == "PI");
        Draw(PipeSymbol.PressureSwitch).OfType<TextEntity>().ShouldContain(t => t.Value == "PS");
        var pump = Draw(PipeSymbol.Pump);
        pump.OfType<Circle>().ShouldNotBeEmpty();
        pump.OfType<Solid>().ShouldNotBeEmpty();
        Draw(PipeSymbol.Filter).OfType<LwPolyline>().ShouldContain(p => p.Vertices.Count == 4);
        GostSymbols.Name(PipeSymbol.Gate).ShouldContain("задвижка");
        GostSymbols.FromKind("butterfly").ShouldBe(PipeSymbol.Butterfly);
    }
}
