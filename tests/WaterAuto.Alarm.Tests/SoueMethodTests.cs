using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// Способ оповещения по табл. Б.1 СП 3.13130.2026: подземная стоянка — порог 50 машино-мест; выбор — проектировщика, программа проверяет.
public class SoueMethodTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    [Fact]
    public void Underground_parking_below_threshold_allows_sound_and_at_threshold_requires_speech()
    {
        SoueMethod.Check(WarningMethod.Sound, "sp3.tB_1m.undergroundParking", 40, Norms).ShouldBeEmpty();

        var tooMany = SoueMethod.Check(WarningMethod.Sound, "sp3.tB_1m.undergroundParking", 50, Norms).ShouldHaveSingleItem();
        tooMany.Code.ShouldBe("soue.method.speechRequired");
        tooMany.Level.ShouldBe(IssueLevel.Error);
        tooMany.Clause!.ShouldContain("табл. Б.1");

        SoueMethod.Check(WarningMethod.Speech, "sp3.tB_1m.undergroundParking", 120, Norms).ShouldBeEmpty();
    }

    [Fact]
    public void Speech_below_threshold_is_allowed_with_a_note_and_rows_without_threshold_follow_flags()
    {
        var note = SoueMethod.Check(WarningMethod.Speech, "sp3.tB_1m.undergroundParking", 30, Norms).ShouldHaveSingleItem();
        note.Level.ShouldBe(IssueLevel.Info);

        // Наземная закрытая стоянка: звуковой, заменяемый речевым — оба допустимы.
        SoueMethod.Check(WarningMethod.Sound, "sp3.tB_1m.abovegroundParking", 0, Norms).ShouldBeEmpty();
        SoueMethod.Check(WarningMethod.Speech, "sp3.tB_1m.abovegroundParking", 0, Norms).ShouldBeEmpty();
        // 50 и более человек — только речевой.
        SoueMethod.Check(WarningMethod.Sound, "sp3.tB_1m.crowd50", 80, Norms).ShouldHaveSingleItem().Code.ShouldBe("soue.method.speechRequired");
    }

    [Fact]
    public void Unknown_row_is_an_error()
    {
        SoueMethod.Check(WarningMethod.Sound, "sp3.tB_1m.nope", 0, Norms).ShouldHaveSingleItem().Code.ShouldBe("soue.method.noRow");
    }
}
