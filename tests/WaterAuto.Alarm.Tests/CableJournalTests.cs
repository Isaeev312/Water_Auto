using WaterAuto.Alarm;
using WaterAuto.Core;

namespace WaterAuto.Alarm.Tests;

// Кабельный журнал: длины по плану (кольцо ДПЛС), сечение по паспортной таблице длин, условные линии питания и сигналов.
public class CableJournalTests
{
    private static readonly CableCatalog Cables = new(1.1, 3, "КПСнг(А)-FRLS 1×2×{section}",
    [
        new(0.2, new Dictionary<double, double> { [16] = 650, [32] = 330, [48] = 220, [64] = 160 }),
        new(0.5, new Dictionary<double, double> { [16] = 1620, [32] = 820, [48] = 550, [64] = 400 }),
        new(1.5, new Dictionary<double, double> { [16] = 1250, [32] = 1250, [48] = 1250, [64] = 1240 }),
    ], "РЭ КДЛ табл. 1.4.2.6.1", "КПСнг(А)-FRLS 2×2×0,75", "ВВГнг(А)-FRLS 3×1,5", "КПСнг(А)-FRLS 1×2×0,5");

    [Fact]
    public void Ring_tour_is_manhattan_through_all_devices_and_back()
    {
        // Пост в (0,0); устройства (10,0), (10,10), (0,10) м → 10 + 10 + 10 + 10 = 40 м.
        var tour = CableJournal.Tour(new PlanPoint(0, 0), [new(10000, 0), new(10000, 10000), new(0, 10000)]);

        tour.ShouldBe(40000, 1e-9);
    }

    [Fact]
    public void Loop_section_is_the_smallest_whose_passport_length_covers_the_ring()
    {
        // Кольцо 40 м × 1,1 = 44 м при 20 мА → столбец 32 мА: 0,2 мм² даёт 330 м — хватает.
        var loop = new LoopLine(1, [new(10000, 0), new(10000, 10000), new(0, 10000)], new Current(0.02), false);
        var result = CableJournal.Build(new PlanPoint(0, 0), [loop], Cables, null, 0, "С2000-КДЛ", "РИП-12", "С2000-АР8");

        var dpls = result.Lines.Single(l => l.Id == "ДПЛС1");
        dpls.Cable.ShouldBe("КПСнг(А)-FRLS 1×2×0,2");
        dpls.LengthM.ShouldBe(44);
        result.Lines.ShouldContain(l => l.Id == "П1" && l.LengthM == 3);
        result.Lines.Count(l => l.Id.StartsWith('С')).ShouldBe(2);
        result.Issues.ShouldNotContain(i => i.Code == "aps.cable.loopTooLong");
    }

    [Fact]
    public void Long_ring_with_high_load_takes_a_bigger_section_or_warns()
    {
        // Кольцо ~ 2 × (900 + 100) = 2000 м × 1,1 = 2200 м при 60 мА: 1,5 мм² даёт 1240 м — не хватает → предупреждение, марка наибольшего сечения.
        var loop = new LoopLine(1, [new(900000, 0), new(900000, 100000)], new Current(0.06), false);
        var result = CableJournal.Build(new PlanPoint(0, 0), [loop], Cables, null, 0, "С2000-КДЛ", "РИП-12", "С2000-АР8");

        result.Lines.Single(l => l.Id == "ДПЛС1").Cable.ShouldBe("КПСнг(А)-FRLS 1×2×1,5");
        result.Issues.ShouldContain(i => i.Code == "aps.cable.loopTooLong" && i.Level == IssueLevel.Warning);

        // Кольцо 440 м при 60 мА: 0,2 — 160, 0,5 — 400, 1,5 — 1240 → 1,5 мм².
        var mid = new LoopLine(1, [new(100000, 0), new(100000, 100000)], new Current(0.06), false);
        CableJournal.Build(new PlanPoint(0, 0), [mid], Cables, null, 0, "С2000-КДЛ", "РИП-12", "С2000-АР8").Lines.Single(l => l.Id == "ДПЛС1").Cable.ShouldBe("КПСнг(А)-FRLS 1×2×1,5");
    }

    [Fact]
    public void Pump_room_adds_module_point_to_the_ring_and_signal_lines()
    {
        var pumpRoom = new Polygon([new(50000, 0), new(60000, 0), new(60000, 8000), new(50000, 8000)]);
        var loop = new LoopLine(1, [new(10000, 0)], new Current(0.01), true);

        var result = CableJournal.Build(new PlanPoint(0, 0), [loop], Cables, pumpRoom, 5, "С2000-КДЛ", "РИП-12", "С2000-АР8");

        // Кольцо: 0→(10,0)→центр насосной (55,4)→0 = 10 + 49 + 59 = 118 м × 1,1 ≈ 130 м.
        result.Lines.Single(l => l.Id == "ДПЛС1").LengthM.ShouldBe(130);
        var sig = result.Lines.Single(l => l.Id == "СИГ");
        // Полупериметр 18 м × 1,1 ≈ 20 м на линию × 5.
        sig.LengthM.ShouldBe(100);
        result.Issues.ShouldContain(i => i.Code == "aps.cable.signalsEstimate");
    }
}
