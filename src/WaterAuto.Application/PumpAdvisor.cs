using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>
/// Кандидат на комплектную насосную установку для окна подбора: обозначение, количество насосов, напор по кривой
/// при требуемой подаче, подходит ли по напору и запас, кривая Q–H одного насоса для графика, габариты
/// гидравлической части и прибора управления (для проверки помещения), ось коллекторов.
/// </summary>
public sealed record PumpSetCandidate(
    string Designation,
    string Pump,
    int MainPumps,
    int StandbyPumps,
    double PowerKw,
    int CollectorDn,
    Footprint Hydraulic,
    Footprint Cabinet,
    double CollectorAxisMm,
    string Maker,
    double? HeadAtDutyMeters,
    bool Fits,
    double MarginMeters,
    IReadOnlyList<PumpCurvePoint> Curve);

/// <summary>
/// Подбор установки для интерфейса: те же правила, что в расчёте (<see cref="PumpSetSelection"/>): установка подходит,
/// если подача в пределах кривой и напор по кривой не меньше требуемого. Сортировка — как в автоподборе
/// (сначала подходящие: меньшая мощность, затем меньший запас напора), варианты без запаса — после них.
/// </summary>
public static class PumpAdvisor
{
    /// <summary>
    /// Все установки каталога с оценкой при требуемых подаче (м³/ч) и напоре (м). Габариты и высота оси коллекторов
    /// берутся из каталога без пересчёта — по ним проектировщик проверяет помещение насосной.
    /// </summary>
    public static IReadOnlyList<PumpSetCandidate> Candidates(string normsDir, double requiredFlowM3h, double requiredHeadM)
    {
        ArgumentException.ThrowIfNullOrEmpty(normsDir);
        var list = new List<PumpSetCandidate>();
        foreach (var set in PumpCatalog.Sets(normsDir))
        {
            var head = PumpSetSelection.HeadAt(set, requiredFlowM3h);
            var fits = head is { } h && h >= requiredHeadM;
            list.Add(new PumpSetCandidate(
                set.Designation,
                set.Pump,
                set.MainPumps,
                set.StandbyPumps,
                set.PowerKw,
                set.CollectorDn,
                set.Hydraulic,
                set.Cabinet,
                set.CollectorAxisMm,
                set.Maker,
                head,
                fits,
                head is { } value ? value - requiredHeadM : 0,
                [.. set.PumpCurve.Select(p => new PumpCurvePoint(p.QM3h, p.HM))]));
        }

        return
        [
            .. list
                .OrderByDescending(c => c.Fits)
                .ThenBy(c => c.Fits ? c.PowerKw * c.MainPumps : double.MaxValue)
                .ThenBy(c => c.Fits ? c.MarginMeters : double.MaxValue)
                .ThenBy(c => c.PowerKw * c.MainPumps)
                .ThenBy(c => c.Designation, StringComparer.Ordinal),
        ];
    }
}
