using System.Globalization;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Таблица соответствия требованиям норм для экспертизы: основные параметры АУП (интенсивность, площадь, расход,
/// продолжительность, шаг, скорость, напор насоса) — нормативное значение из данных норм против принятого по расчёту,
/// затем замечания расчёта уровня «предупреждение» и «ошибка» с их пунктами. Чисел норм здесь нет.
/// </summary>
public static class ComplianceComposer
{
    private const string Sp485 = "СП 485.1311500.2020";
    private const double Eps = 1e-6;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static IReadOnlyList<ComplianceRow> Compose(DesignResult design, double? pumpHeadAtDuty, IReadOnlyList<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(issues);
        var p = design.Parameters;
        var source = $"{Sp485}, {p.Source}";
        var intensity = design.ZoneArea > 0 ? design.DesignFlow.LitersPerSecond / design.ZoneArea : 0;
        var spacing = Math.Max(design.RowSpacing.Meters, design.LineSpacing.Meters);
        static ComplianceVerdict Ok(bool ok) => ok ? ComplianceVerdict.Complies : ComplianceVerdict.Violates;
        string F(double v, string format) => v.ToString(format, Ru);

        var rows = new List<ComplianceRow>
        {
            new("Интенсивность орошения (средняя по расчётной площади)", source,
                $"не менее {F(p.Intensity, "0.###")} л/(с·м²)", $"{F(intensity, "0.###")} л/(с·м²)", Ok(intensity >= p.Intensity - Eps)),
            new("Площадь для расчёта расхода", source,
                $"не менее {F(p.MinArea, "0")} м²", $"{F(design.ZoneArea, "0.#")} м²", Ok(design.ZoneArea >= p.MinArea - Eps)),
            // Б.24: расчётный расход меньше нормативного — принимается нормативный; больше — расчётный (Б.26).
            new("Расход АУП", source + ", Б.24",
                $"не менее {F(design.NormativeFlow.LitersPerSecond, "0.##")} л/с",
                $"{F(Math.Max(design.DesignFlow.LitersPerSecond, design.NormativeFlow.LitersPerSecond), "0.##")} л/с (по гидравлическому расчёту {F(design.DesignFlow.LitersPerSecond, "0.##")} л/с)",
                ComplianceVerdict.Complies),
            new("Продолжительность подачи воды", source,
                $"{F(p.Duration, "0")} мин", $"{F(design.Duration, "0")} мин", Ok(design.Duration >= p.Duration - Eps)),
            new("Шаг оросителей", $"{Sp485}, табл. 6.1",
                $"не более {F(p.MaxSpacing.Meters, "0.##")} м", $"{F(spacing, "0.##")} м", Ok(spacing <= p.MaxSpacing.Meters + Eps)),
        };
        if (design.SupplyPipe is not null && design.VelocityLimit > 0)
        {
            rows.Add(new("Скорость в подводящем трубопроводе", $"{Sp485}, п. 6.7.1.37",
                $"не более {F(design.VelocityLimit, "0.#")} м/с", $"{F(design.SupplyVelocity, "0.##")} м/с",
                Ok(design.SupplyVelocity <= design.VelocityLimit + Eps)));
        }

        rows.Add(pumpHeadAtDuty is { } head
            ? new("Напор насосной установки при расчётном расходе", $"{Sp485}, прил. Б (Б.23–Б.30)",
                $"не менее {F(design.PumpHead.Meters, "0.0")} м", $"{F(head, "0.0")} м по характеристике каталога", Ok(head >= design.PumpHead.Meters - Eps))
            : new("Напор насосной установки при расчётном расходе", $"{Sp485}, прил. Б (Б.23–Б.30)",
                $"не менее {F(design.PumpHead.Meters, "0.0")} м", "установка не подобрана", ComplianceVerdict.Check));

        foreach (var issue in issues.Where(i => i.Level >= IssueLevel.Warning).DistinctBy(i => (i.Code, i.Message)))
        {
            rows.Add(new(issue.Message, issue.Clause ?? "—", "—", "—",
                issue.Level == IssueLevel.Error ? ComplianceVerdict.Violates : ComplianceVerdict.Check));
        }

        return rows;
    }
}
