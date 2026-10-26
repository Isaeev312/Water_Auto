using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Откуда взят Kт — пишется в журнал расчёта (ARCHITECTURE §9.2).</summary>
public enum CharacteristicSource
{
    TableB2,
    FormulaB8,
}

/// <param name="OutdoorOnly">Строка табл. Б.2 отмечена «*» — трубы сетей наружного водоснабжения; для внутренней сети нужно замечание.</param>
public sealed record ResolvedCharacteristic(PipeCharacteristic Value, CharacteristicSource Source, string NormId, bool OutdoorOnly = false);

/// <summary>
/// Kт стальной трубы: строка табл. Б.2 по стандарту, наружному диаметру и стенке; если строки нет —
/// формула Б.8 Kт = 10⁻⁸·ψ·Ø^θ по внутреннему диаметру с коэффициентами табл. Б.3 — только в её диапазоне DN.
/// Вне таблицы и вне диапазона формулы — null: Kт задаётся вручную или по данным производителя.
/// Правило выбора — ARCHITECTURE §9.2, подтверждение — open_questions.md В-04.
/// </summary>
public static class PipeCharacteristics
{
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

        foreach (var r in norms.ByClause("табл. Б.2"))
        {
            if (r.Attributes.GetValueOrDefault("standard") == standard
                && Math.Abs(r.Values["D_out"]!.Value - outerDiameterMm) < 1e-9
                && Math.Abs(r.Values["wall"]!.Value - wallMm) < 1e-9)
            {
                var outdoorOnly = string.Equals(r.Attributes.GetValueOrDefault("outdoorOnly"), "true", StringComparison.OrdinalIgnoreCase);
                return new ResolvedCharacteristic(new PipeCharacteristic(r.Values["Kt"]!.Value), CharacteristicSource.TableB2, r.Id, outdoorOnly);
            }
        }

        var coefficients = norms.ByClause("табл. Б.3").FirstOrDefault(r =>
            r.Attributes.GetValueOrDefault("standard") == standard && r.Attributes.GetValueOrDefault("quantity") == "Kt");
        if (coefficients?.Values.GetValueOrDefault("dnMin") is not { } dnMin || coefficients.Values.GetValueOrDefault("dnMax") is not { } dnMax
            || dn < dnMin || dn > dnMax)
        {
            // Нет коэффициентов или не задан диапазон DN формулы — считать нельзя (раньше null в dnMin/dnMax молча пропускал проверку).
            return null;
        }

        var inner = outerDiameterMm - (2 * wallMm);
        var kt = 1e-8 * coefficients.Values["psi"]!.Value * Math.Pow(inner, coefficients.Values["theta"]!.Value);
        if (!double.IsFinite(kt) || kt <= 0)
        {
            return null;
        }

        return new ResolvedCharacteristic(new PipeCharacteristic(kt), CharacteristicSource.FormulaB8, coefficients.Id);
    }
}
