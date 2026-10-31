using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>
/// Речевая СОУЭ (ADR 0012, этап 3): уровень громкоговорителя на 1 м — по формуле В.1 СП 3.13130.2026 от чувствительности и
/// отвода; линии 100 В — по группам (уровням): мощность линии — сумма потребляемой мощности отводов, длина — цепочка от поста
/// через громкоговорители (ближайший сосед, расстояния по осям) плюс стояк, сечение — по формуле РЭ усилителя
/// S = 2·ρ·I·L/ΔU (пополам при равномерном размещении) при ΔU не более В.3.4; число усилителей — по паспортной мощности,
/// числу ответвлений. Паспортные пределы — каталог, норма — только ΔU и коэффициент В.1.
/// </summary>
public static class SpeechLines
{
    private const string Doc = "СП 3.13130.2026";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Отвод по заданной акустической мощности (Вт); null — наибольший; нет такого отвода — null.</summary>
    public static SpeakerTap? ChooseTap(SpeechSpeaker speaker, double? acousticW)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        return acousticW is { } w ? speaker.Taps.FirstOrDefault(t => Math.Abs(t.AcousticW - w) < 1e-9) : speaker.Taps.MaxBy(t => t.AcousticW);
    }

    /// <summary>В.1: L1 = S + k·lg P, k — из данных норм (10 дБ на декаду мощности); нет записи — NaN.</summary>
    public static double LevelAt1m(SpeechSpeaker speaker, SpeakerTap tap, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(tap);
        ArgumentNullException.ThrowIfNull(norms);
        var law = norms.Find("sp3.pB_formulas.attenuation");
        return law?.Values.GetValueOrDefault("powerLawCoefficient") is { } k ? speaker.SensitivityDb + (k * Math.Log10(tap.AcousticW)) : double.NaN;
    }

    /// <param name="groups">Группы громкоговорителей — по одной линии на группу: имя (уровень), точки на плане, длина стояка до группы, мм.</param>
    /// <param name="panel">Точка поста (усилителя) на плане; null — длины не определены.</param>
    public static SpeechPlan Build(IReadOnlyList<(string Name, IReadOnlyList<PlanPoint> Speakers, double RiserMm)> groups, SpeechSpeaker speaker, SpeakerTap tap, SpeechAmplifier amplifier, LineMonitor? monitor, CableCatalog cables, PlanPoint? panel, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(tap);
        ArgumentNullException.ThrowIfNull(amplifier);
        ArgumentNullException.ThrowIfNull(cables);
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        var drop = norms.Find("sp3.pB_3_4.voltageDrop");
        if (drop is null)
        {
            issues.Add(new Issue("soue.noNorm", IssueLevel.Error, "В данных норм нет записи sp3.pB_3_4.voltageDrop (падение напряжения в линии оповещения) — сечение линий не выбрано."));
        }

        var level = LevelAt1m(speaker, tap, norms);
        if (double.IsNaN(level))
        {
            issues.Add(new Issue("soue.noNorm", IssueLevel.Error, "В данных норм нет коэффициента формулы В.1 (powerLawCoefficient) — уровень громкоговорителя на 1 м не определён."));
        }

        if (panel is null)
        {
            issues.Add(new Issue("soue.speech.noPanel", IssueLevel.Warning, "Линии речевого оповещения: без точки поста (alarm.panel) длины и сечения линий не определены."));
        }

        var lines = new List<SpeechLine>();
        var n = 0;
        foreach (var (name, points, riser) in groups.Where(g => g.Speakers.Count > 0))
        {
            n++;
            var consumed = points.Count * tap.ConsumedW;
            var acoustic = points.Count * tap.AcousticW;
            double length = 0;
            double? section = null;
            if (panel is { } start)
            {
                length = Math.Round((Chain(start, points) + riser) / 1000 * cables.Reserve, 0);
                if (length > amplifier.MaxSegmentM)
                {
                    issues.Add(new Issue("soue.speech.segment", IssueLevel.Warning,
                        string.Create(Ru, $"Линия оповещения «{name}»: длина {length:0} м больше паспортного сегмента {amplifier.MaxSegmentM:0} м для {amplifier.Name} — разделить на ответвления (до {amplifier.MaxBranches}) или поставить усилитель ближе."),
                        Clause: amplifier.Source));
                }

                if (drop is not null && cables.SpeechSections is { Count: > 0 } sections && cables.Resistivity is { } rho)
                {
                    // I = P/U; ΔU = maxDrop % от U; S = 2·ρ·I·L/ΔU, пополам при равномерном размещении РО вдоль линии (РЭ).
                    var current = consumed / amplifier.LineVoltage;
                    var deltaU = amplifier.LineVoltage * drop.Values["maxDrop"]!.Value / 100;
                    var required = 2 * rho * current * length / deltaU / 2;
                    section = sections.Order().FirstOrDefault(s => s >= required - 1e-9);
                    if (section == 0)
                    {
                        section = null;
                        issues.Add(new Issue("soue.speech.section", IssueLevel.Warning,
                            string.Create(Ru, $"Линия оповещения «{name}»: нужно сечение {required:0.##} мм² при ΔU {drop.Values["maxDrop"]:0} % — больше ряда каталога ({sections.Max():0.##} мм²); разделить линию."),
                            Clause: Doc + ", прил. В, п. В.3.4"));
                    }
                }
            }

            lines.Add(new SpeechLine(n, name, points.Count, consumed, acoustic, length, section));
        }

        var totalConsumed = lines.Sum(l => l.ConsumedW);
        var totalAcoustic = lines.Sum(l => l.AcousticW);
        var amplifiers = lines.Count == 0 ? 0 : Math.Max(1, Math.Max((int)Math.Ceiling(totalConsumed / amplifier.MaxConsumedW), Math.Max((int)Math.Ceiling(totalAcoustic / amplifier.MaxAcousticW), (int)Math.Ceiling((double)lines.Count / amplifier.MaxBranches))));
        if (amplifiers > 0)
        {
            issues.Add(new Issue("soue.speech.amplifiers", IssueLevel.Info,
                string.Create(Ru, $"Речевое оповещение: {lines.Sum(l => l.Speakers)} громкоговорителей на отводе {tap.AcousticW:0.#} Вт ({tap.ConsumedW:0.##} Вт от линии), уровень на 1 м по В.1 — {level:0.#} дБ; {lines.Count} лини{(lines.Count == 1 ? "я" : "и")} 100 В, {totalConsumed:0.#} Вт; принято {amplifiers} × {amplifier.Name} (до {amplifier.MaxConsumedW:0} Вт, {amplifier.MaxBranches} ответвлений){(monitor is not null ? $", контроль линий — {monitor.Name} по одному на линию" : "")}. Резерв питания блока — встроенные АКБ {amplifier.Batteries} × {amplifier.BatteryName}: {amplifier.BackupStandbyH:0} ч дежурный, {amplifier.BackupAlarmH:0} ч оповещение по паспорту; в расчёт АКБ РИП не входит. Распределение громкоговорителей по усилителям — проектировщик."),
                Clause: amplifier.Source));
        }

        return new SpeechPlan(amplifiers, lines, tap, level, totalConsumed, issues);
    }

    /// <summary>Цепочка от поста через все точки к ближайшей непосещённой (без возврата), расстояния по осям, мм.</summary>
    private static double Chain(PlanPoint start, IReadOnlyList<PlanPoint> points)
    {
        var left = points.ToList();
        var at = start;
        var total = 0.0;
        while (left.Count > 0)
        {
            var next = left.MinBy(p => Math.Abs(at.XMm - p.XMm) + Math.Abs(at.YMm - p.YMm));
            total += Math.Abs(at.XMm - next.XMm) + Math.Abs(at.YMm - next.YMm);
            left.Remove(next);
            at = next;
        }

        return total;
    }
}
