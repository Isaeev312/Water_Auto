using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Откуда взят Kт — пишется в журнал расчёта (ARCHITECTURE §9.2).</summary>
public enum CharacteristicSource
{
    TableB2,
    FormulaB8,
}

public sealed record ResolvedCharacteristic(PipeCharacteristic Value, CharacteristicSource Source, string NormId);

/// <summary>
/// Kт стальной трубы: строка табл. Б.2 по стандарту, наружному диаметру и стенке; если строки нет —
/// формула Б.8 Kт = 10⁻⁸·ψ·Ø^θ по внутреннему диаметру с коэффициентами табл. Б.3.
/// Правило выбора — ARCHITECTURE §9.2, подтверждение — open_questions.md В-04.
/// </summary>
public static class PipeCharacteristics
{
    public static ResolvedCharacteristic? Resolve(INormsProvider norms, string standard, double outerDiameterMm, double wallMm)
    {
        ArgumentNullException.ThrowIfNull(norms);
        foreach (var r in norms.ByClause("табл. Б.2"))
        {
            if (r.Attributes.GetValueOrDefault("standard") == standard
                && Math.Abs(r.Values["D_out"]!.Value - outerDiameterMm) < 1e-9
                && Math.Abs(r.Values["wall"]!.Value - wallMm) < 1e-9)
            {
                return new ResolvedCharacteristic(new PipeCharacteristic(r.Values["Kt"]!.Value), CharacteristicSource.TableB2, r.Id);
            }
        }

        var coefficients = norms.ByClause("табл. Б.3").FirstOrDefault(r =>
            r.Attributes.GetValueOrDefault("standard") == standard && r.Attributes.GetValueOrDefault("quantity") == "Kt");
        if (coefficients is null)
        {
            return null;
        }

        var inner = outerDiameterMm - (2 * wallMm);
        var kt = 1e-8 * coefficients.Values["psi"]!.Value * Math.Pow(inner, coefficients.Values["theta"]!.Value);
        return new ResolvedCharacteristic(new PipeCharacteristic(kt), CharacteristicSource.FormulaB8, coefficients.Id);
    }
}
