using System.Globalization;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Таблица соответствия требованиям норм для экспертизы. По каждой группе помещений — расчёт с наибольшим давлением насоса
/// (тот же, что в записке): нормативные величины, принятые по таблицам (вывод «принято по норме»), и проверяемые —
/// площадь диктующей зоны, шаг оросителей, скорость в подводящем; затем напор подобранной установки по огибающей
/// и замечания расчёта АУПТ уровня «предупреждение» и «ошибка» с их пунктами. Чисел норм здесь нет.
/// </summary>
public static class ComplianceComposer
{
    private const string Sp485 = "СП 485.1311500.2020";
    private const double Eps = 1e-6;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    // Коды замечаний других систем (АПС, СОУЭ, газ, порошок) — у них свои тома; в таблицу АУПТ не входят.
    private static readonly string[] OtherSystems = ["aps.", "soue.", "gas.", "powder.", "alarm."];

    public static IReadOnlyList<ComplianceRow> Compose(IReadOnlyList<DesignResult> designs, DesignResult worst, double? pumpHeadAtDuty, IReadOnlyList<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(designs);
        ArgumentNullException.ThrowIfNull(worst);
        ArgumentNullException.ThrowIfNull(issues);
        static ComplianceVerdict Ok(bool ok) => ok ? ComplianceVerdict.Complies : ComplianceVerdict.Violates;
        static string F(double v, string format) => v.ToString(format, Ru);

        var rows = new List<ComplianceRow>();
        var byGroup = designs.GroupBy(d => d.Parameters.Group).Select(g => g.MaxBy(d => d.PumpPressure.Mpa)!).ToList();
        foreach (var design in byGroup)
        {
            var p = design.Parameters;
            var suffix = byGroup.Count > 1 ? $" (группа {p.Group})" : "";
            var source = $"{Sp485}, {p.Source}";
            var spacing = Math.Max(design.RowSpacing.Meters, design.LineSpacing.Meters);
            rows.Add(new("Интенсивность орошения" + suffix, source + ", Б.1",
                $"{F(p.Intensity, "0.###")} л/(с·м²)", $"расход диктующего оросителя q₁ = {F(design.DictatingFlow.LitersPerSecond, "0.00")} л/с по Б.1 от нормативной интенсивности",
                ComplianceVerdict.Adopted));
            rows.Add(new("Площадь для расчёта расхода" + suffix, source + ", Б.1.2.1",
                $"не менее {F(p.MinArea, "0")} м²", $"{F(design.ZoneArea, "0.#")} м² ({design.ZoneSprinklers.Count} оросителей диктующей зоны)",
                Ok(design.ZoneArea >= p.MinArea - Eps)));
            // Б.24: расчётный расход меньше нормативного — принимается нормативный; больше — расчётный (Б.26).
            rows.Add(new("Расход АУП" + suffix, source + ", Б.24, Б.26",
                $"не менее {F(design.NormativeFlow.LitersPerSecond, "0.##")} л/с",
                $"{F(Math.Max(design.DesignFlow.LitersPerSecond, design.NormativeFlow.LitersPerSecond), "0.##")} л/с (по гидравлическому расчёту {F(design.DesignFlow.LitersPerSecond, "0.##")} л/с)",
                ComplianceVerdict.Adopted));
            rows.Add(new("Продолжительность подачи воды" + suffix, source, $"{F(p.Duration, "0")} мин", $"{F(design.Duration, "0")} мин", ComplianceVerdict.Adopted));
            rows.Add(new("Шаг оросителей" + suffix, $"{Sp485}, табл. 6.1",
                $"не более {F(p.MaxSpacing.Meters, "0.##")} м", $"{F(spacing, "0.##")} м", Ok(spacing <= p.MaxSpacing.Meters + Eps)));
            if (design.SupplyPipe is not null && design.VelocityLimit > 0)
            {
                rows.Add(new("Скорость в подводящем трубопроводе" + suffix, $"{Sp485}, п. 6.7.1.37",
                    $"не более {F(design.VelocityLimit, "0.#")} м/с", $"{F(design.SupplyVelocity, "0.##")} м/с",
                    Ok(design.SupplyVelocity <= design.VelocityLimit + Eps)));
            }
        }

        rows.Add(pumpHeadAtDuty is { } head
            ? new("Напор насосной установки при расчётном расходе", $"{Sp485}, прил. Б (Б.23–Б.30)",
                $"не менее {F(worst.PumpHead.Meters, "0.0")} м при {F(worst.PumpFlow.LitersPerSecond, "0.##")} л/с", $"{F(head, "0.0")} м по характеристике каталога",
                Ok(head >= worst.PumpHead.Meters - Eps))
            : new("Напор насосной установки при расчётном расходе", $"{Sp485}, прил. Б (Б.23–Б.30)",
                $"не менее {F(worst.PumpHead.Meters, "0.0")} м при {F(worst.PumpFlow.LitersPerSecond, "0.##")} л/с", "установка не подобрана", ComplianceVerdict.Check));

        foreach (var issue in issues
            .Where(i => i.Level >= IssueLevel.Warning && !OtherSystems.Any(s => i.Code.StartsWith(s, StringComparison.Ordinal)))
            .DistinctBy(i => (i.Code, i.Message)))
        {
            rows.Add(new(issue.Message, issue.Clause ?? "—", "—", "—",
                issue.Level == IssueLevel.Error ? ComplianceVerdict.Violates : ComplianceVerdict.Check));
        }

        return rows;
    }
}
