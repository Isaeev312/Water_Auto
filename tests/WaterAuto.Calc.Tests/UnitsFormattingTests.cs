using WaterAuto.Core;

namespace WaterAuto.Calc.Tests;

// Подписи величин идут в замечания, записку и чертежи на русском: дробная часть — через запятую при любой культуре потока.
public class UnitsFormattingTests
{
    [Fact]
    public void Quantities_with_units_print_with_a_decimal_comma()
    {
        new Length(0.5).ToString().ShouldBe("0,5 м");
        new Flow(1.25).ToString().ShouldBe("1,25 л/с");
        new Pressure(0.105).ToString().ShouldBe("0,105 МПа");
        new Velocity(2.5).ToString().ShouldBe("2,5 м/с");
    }
}
