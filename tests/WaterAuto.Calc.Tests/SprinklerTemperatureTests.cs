using WaterAuto.Calc.Layout;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class SprinklerTemperatureTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    [Fact]
    public void Lowest_nominal_whose_ambient_limit_covers_the_room_is_chosen()
    {
        // Среда до 40 °С: 57 °С допускает не более 38 °С — мало; 68 °С — до 50 °С (табл. 6.4); колба красная (ГОСТ Р 51043, табл. 2).
        var r = SprinklerTemperature.Choose(Norms, 40, null);
        r.Choice!.Nominal.ShouldBe(68);
        r.Choice.MaxAmbient.ShouldBe(50);
        r.Choice.Color.ShouldBe("красный");
        r.Choice.ResponseTime.ShouldBe(300);
    }

    [Fact]
    public void Only_temperatures_offered_by_the_catalog_are_used()
    {
        // Каталог модели: 57, 79, 93 °С. Для 40 °С подходит 79 °С (до 58 °С).
        var r = SprinklerTemperature.Choose(Norms, 40, [57, 79, 93]);
        r.Choice!.Nominal.ShouldBe(79);
        r.Choice.Color.ShouldBe("жёлтый");
    }

    [Fact]
    public void Missing_ambient_temperature_is_a_warning_without_a_guess()
    {
        var r = SprinklerTemperature.Choose(Norms, null, null);
        r.Choice.ShouldBeNull();
        r.Issues.ShouldContain(i => i.Code == "sprinkler.temperature" && i.Level == IssueLevel.Warning);
    }

    [Fact]
    public void Ambient_above_the_table_is_an_error()
    {
        SprinklerTemperature.Choose(Norms, 320, null).Issues.ShouldContain(i => i.Level == IssueLevel.Error);
    }
}
