using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// Речевая СОУЭ: уровень на 1 м по В.1, линии 100 В по группам, усилители по паспортной мощности, сечение по формуле РЭ при ΔU ≤ 10 % (В.3.4).
public class SpeechLinesTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static readonly SpeechSpeaker Speaker = new(91, [new(3, 3.75), new(1, 1.25)], 100);
    private static readonly SpeechAmplifier Amp = new("Рупор-300", "блок речевого оповещения", "Болид", 300, 240, 20, 400, 100, "АБ 1217С", 2, 24, 4, 340, "РЭ", "серт.");
    private static readonly LineMonitor Monitor = new("Рупор-300-МК", "модуль контроля линии", "Болид", new Current(0.002), "РЭ", "серт.");
    private static readonly CableCatalog Cables = new(1.1, 3, "ДПЛС {section}", [new LoopCableSection(0.5, new Dictionary<double, double> { [64] = 400 })], "РЭ", "П", "С", "СИГ",
        "КПСнг(А)-FRLS 1×2×{section}", [0.5, 0.75, 1.0, 1.5, 2.5], 0.0171, "РЭ Рупор-300");

    private static List<PlanPoint> Row(int n, double y) => [.. Enumerable.Range(0, n).Select(i => new PlanPoint(5000 + (i * 10000), y))];

    [Fact]
    public void Level_at_1m_follows_B1_for_the_chosen_tap()
    {
        // 91 дБ при 1 Вт → на отводе 3 Вт: 91 + 10·lg 3 = 95,8 дБ; отвод по умолчанию — наибольший.
        SpeechLines.LevelAt1m(Speaker, Speaker.Taps[0], Norms).ShouldBe(91 + (10 * Math.Log10(3)), 1e-9);
        SpeechLines.ChooseTap(Speaker, null).ShouldBe(Speaker.Taps[0]);
        SpeechLines.ChooseTap(Speaker, 1).ShouldBe(Speaker.Taps[1]);
        SpeechLines.ChooseTap(Speaker, 2).ShouldBeNull();
    }

    [Fact]
    public void Lines_per_group_with_power_length_section_and_amplifier_count()
    {
        // Две группы (уровни) по 6 громкоговорителей на отводе 3 Вт: 22,5 Вт на линию, 45 Вт всего — один усилитель.
        var plan = SpeechLines.Build([("−1 этаж", Row(6, 5000), 0.0), ("−2 этаж", Row(6, 5000), 6000.0)], Speaker, Speaker.Taps[0], Amp, Monitor, Cables, new PlanPoint(0, 0), Norms);

        plan.Amplifiers.ShouldBe(1);
        plan.Lines.Count.ShouldBe(2);
        plan.Lines[0].ConsumedW.ShouldBe(22.5, 1e-9);
        plan.Lines[0].AcousticW.ShouldBe(18, 1e-9);
        // Цепочка от поста: 5 + 5 + 5·10 = 60 м; с запасом 1,1 → 66 м; на втором уровне ещё стояк 6 м → 72,6 м.
        plan.Lines[0].LengthM.ShouldBe(66, 0.5);
        plan.Lines[1].LengthM.ShouldBe(72.6, 0.5);
        // I = 22,5/100 = 0,225 А; S = 2·0,0171·0,225·66/10/2 = 0,025 мм² → наименьшее сечение ряда 0,5.
        plan.Lines[0].SectionMm2.ShouldBe(0.5);
        plan.TotalConsumedW.ShouldBe(45, 1e-9);
        plan.Issues.ShouldNotContain(i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Power_above_amplifier_rating_adds_amplifiers_and_long_segments_are_flagged()
    {
        // 90 громкоговорителей × 3,75 Вт = 337,5 Вт > 300 → 2 усилителя; линия 90 точек по 10 м — длиннее 400 м сегмента.
        var plan = SpeechLines.Build([("уровень", Row(90, 5000), 0.0)], Speaker, Speaker.Taps[0], Amp, Monitor, Cables, new PlanPoint(0, 0), Norms);

        plan.Amplifiers.ShouldBe(2);
        plan.Issues.ShouldContain(i => i.Code == "soue.speech.segment" && i.Level == IssueLevel.Warning);
        plan.Issues.ShouldContain(i => i.Code == "soue.speech.amplifiers");

        // Без точки поста длины не определены (0), сечение не выбрано, об этом замечание.
        var noPanel = SpeechLines.Build([("уровень", Row(6, 5000), 0.0)], Speaker, Speaker.Taps[0], Amp, Monitor, Cables, null, Norms);
        noPanel.Lines[0].LengthM.ShouldBe(0);
        noPanel.Lines[0].SectionMm2.ShouldBeNull();
        noPanel.Issues.ShouldContain(i => i.Code == "soue.speech.noPanel");
    }
}
