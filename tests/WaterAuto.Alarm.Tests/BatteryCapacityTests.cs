using WaterAuto.Alarm;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Alarm.Tests;

// Ёмкость АКБ по прил. Б СП 6.13130.2025 (open_questions.md, А-03). Ожидаемые значения посчитаны вручную.
public class BatteryCapacityTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static readonly BatteryLoad Load = new(new Current(0.1), new Current(0.5), new Current(0.02));

    [Fact]
    public void Single_load_factor_follows_formula_B1()
    {
        // Kстр = 100/80 = 1,25; C = 1,25·1,2·(0,1·24 + 0,5·1 + 0,02·25) = 1,5·3,4.
        var result = BatteryCapacity.Required(Load, new BatteryFactors(1.2, 1.2, 80), Norms);

        result.Required!.Value.AmpereHours.ShouldBe(5.1, 1e-9);
        result.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Separate_load_factors_use_alarm_current_in_formula_B1_1()
    {
        // Решение владельца по Б.1.1: во втором слагаемом ток «Пожара».
        // C = 1,25·(1,1·(0,1 + 0,02)·24 + 1,3·(0,5 + 0,02)·1) = 1,25·(3,168 + 0,676).
        var result = BatteryCapacity.Required(Load, new BatteryFactors(1.1, 1.3, 80), Norms);

        result.Required!.Value.AmpereHours.ShouldBe(4.805, 1e-9);
        result.Issues.ShouldHaveSingleItem().Code.ShouldBe("aps.battery.b11Correction");
        result.Issues[0].Level.ShouldBe(IssueLevel.Info);
    }

    [Fact]
    public void Times_come_from_norms_data()
    {
        var norms = new StubNorms(standbyHours: 12, alarmHours: 2);

        // C = 1,25·1,2·(0,1·12 + 0,5·2 + 0,02·14) = 1,5·2,48.
        var result = BatteryCapacity.Required(Load, new BatteryFactors(1.2, 1.2, 80), norms);

        result.Required!.Value.AmpereHours.ShouldBe(3.72, 1e-9);
    }

    [Fact]
    public void Missing_norm_is_an_error_issue()
    {
        var result = BatteryCapacity.Required(Load, new BatteryFactors(1.2, 1.2, 80), new StubNorms(null, 1));

        result.Required.ShouldBeNull();
        result.Issues.ShouldContain(i => i.Code == "aps.battery.noNorm" && i.Level == IssueLevel.Error);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(120.0)]
    public void End_of_life_capacity_outside_percent_range_is_an_error(double percent)
    {
        var result = BatteryCapacity.Required(Load, new BatteryFactors(1.2, 1.2, percent), Norms);

        result.Required.ShouldBeNull();
        result.Issues.ShouldContain(i => i.Code == "aps.battery.endOfLife" && i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Negative_current_and_non_positive_factor_are_errors()
    {
        var load = new BatteryLoad(new Current(-0.1), new Current(0.5), new Current(0.02));

        var result = BatteryCapacity.Required(load, new BatteryFactors(0, 1.2, 80), Norms);

        result.Required.ShouldBeNull();
        result.Issues.Select(i => i.Code).ShouldBe(["aps.battery.current", "aps.battery.loadFactor"], ignoreOrder: true);
    }

    [Fact]
    public void Backup_time_follows_formula_B3()
    {
        // t = 5,1 / (0,5·1,25) = 8,16 ч.
        var result = BatteryCapacity.Backup(new Charge(5.1), new Current(0.5), 80);

        result.Time!.Value.TotalHours.ShouldBe(8.16, 1e-9);
        result.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Backup_time_without_alarm_current_is_an_error()
    {
        var result = BatteryCapacity.Backup(new Charge(5.1), Current.Zero, 80);

        result.Time.ShouldBeNull();
        result.Issues.ShouldHaveSingleItem().Code.ShouldBe("aps.battery.current");
    }

    private sealed class StubNorms(double? standbyHours, double? alarmHours) : INormsProvider
    {
        public NormRecord? Find(string id) => id switch
        {
            BatteryCapacity.StandbyTimeId => Record(id, standbyHours),
            BatteryCapacity.AlarmTimeId => Record(id, alarmHours),
            _ => null,
        };

        public IEnumerable<NormRecord> ByClause(string clause) => [];

        private static NormRecord? Record(string id, double? hours) => hours is null
            ? null
            : new NormRecord(id, "СП 6.13130.2025", "тест", "прил. Б, п. Б.2",
                new Dictionary<string, double?> { ["t"] = hours },
                new Dictionary<string, string> { ["t"] = "ч" },
                new Dictionary<string, string>());
    }
}
