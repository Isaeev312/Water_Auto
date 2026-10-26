using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Откуда взят Kт — пишется в журнал расчёта (ARCHITECTURE §9.2).</summary>
public enum CharacteristicSource
{
    TableB2,
    FormulaB8,
}

/// <summary>Kт трубы и откуда он взят.</summary>
/// <param name="RequestedDn">DN, заявленный для трубы (поле DN проекта).</param>
/// <param name="TableDn">DN строки табл. Б.2; null — Kт по формуле Б.8, строки в таблице не было.</param>
/// <param name="OutdoorOnly">Строка табл. Б.2 отмечена «*» — трубы сетей наружного водоснабжения; для внутренней сети нужно замечание.</param>
public sealed record ResolvedCharacteristic(
    PipeCharacteristic Value,
    CharacteristicSource Source,
    string NormId,
    int RequestedDn,
    int? TableDn = null,
    bool OutdoorOnly = false)
{
    /// <summary>DN совпадает с DN строки табл. Б.2; для формулы Б.8 сверять нечего — true.</summary>
    public bool DnMatches => TableDn is null || TableDn == RequestedDn;
}

/// <summary>
/// Kт стальной трубы: строка таблицы Б.2 по стандарту, наружному диаметру и стенке; если строки нет —
/// формула Б.8 Kт = 10⁻⁸·ψ·Ø^θ по внутреннему диаметру с коэффициентами таблицы Б.3 — только в её диапазоне DN.
/// Вне таблицы и вне диапазона формулы — null: Kт задаётся вручную или по данным производителя.
/// Правило выбора — ARCHITECTURE §9.2, подтверждение — open_questions.md В-04.
/// </summary>
public static class PipeCharacteristics
{
    public static ResolvedCharacteristic? Resolve(INormsProvider norms, PipeGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Resolve(norms, geometry.Standard, geometry.Dn, geometry.OuterDiameter, geometry.Wall);
    }

    public static ResolvedCharacteristic? Resolve(INormsProvider norms, string standard, int dn, Length outerDiameter, Length wall)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var outerDiameterMm = outerDiameter.Millimeters;
        var wallMm = wall.Millimeters;
        if (!double.IsFinite(outerDiameterMm) || !double.IsFinite(wallMm)
            || outerDiameterMm <= 0 || wallMm <= 0 || outerDiameterMm <= 2 * wallMm)
        {
            return null;
        }

        foreach (var r in norms.ByClause(NormKeys.ClauseTableB2).Where(IsUsable))
        {
            if (r.Attributes.GetValueOrDefault(NormKeys.AttrStandard) == standard
                && Math.Abs(r.Values[NormKeys.DOut]!.Value - outerDiameterMm) < 1e-9
                && Math.Abs(r.Values[NormKeys.Wall]!.Value - wallMm) < 1e-9)
            {
                var outdoorOnly = string.Equals(r.Attributes.GetValueOrDefault(NormKeys.AttrOutdoorOnly), "true", StringComparison.OrdinalIgnoreCase);
                return new ResolvedCharacteristic(
                    new PipeCharacteristic(r.Values[NormKeys.Kt]!.Value),
                    CharacteristicSource.TableB2,
                    r.Id,
                    dn,
                    (int)r.Values[NormKeys.Dn]!.Value,
                    outdoorOnly);
            }
        }

        var coefficients = norms.ByClause(NormKeys.ClauseTableB3).FirstOrDefault(r =>
            IsUsable(r) && r.Attributes.GetValueOrDefault(NormKeys.AttrStandard) == standard && r.Attributes.GetValueOrDefault(NormKeys.AttrQuantity) == NormKeys.Kt);
        if (coefficients?.Values.GetValueOrDefault(NormKeys.DnMin) is not { } dnMin || coefficients.Values.GetValueOrDefault(NormKeys.DnMax) is not { } dnMax
            || dn < dnMin || dn > dnMax)
        {
            // Нет коэффициентов или не задан диапазон DN формулы — считать нельзя (раньше null в dnMin/dnMax молча пропускал проверку).
            return null;
        }

        var inner = outerDiameterMm - (2 * wallMm);
        var kt = 1e-8 * coefficients.Values[NormKeys.Psi]!.Value * Math.Pow(inner, coefficients.Values[NormKeys.Theta]!.Value);
        if (!double.IsFinite(kt) || kt <= 0)
        {
            return null;
        }

        return new ResolvedCharacteristic(new PipeCharacteristic(kt), CharacteristicSource.FormulaB8, coefficients.Id, dn);
    }

    // attrs.usable == "false" — строка неприменима: Б.7 не воспроизводит табл. Б.1 (сноска в table_B_3.json).
    private static bool IsUsable(NormRecord record) =>
        !string.Equals(record.Attributes.GetValueOrDefault(NormKeys.AttrUsable), "false", StringComparison.OrdinalIgnoreCase);
}
