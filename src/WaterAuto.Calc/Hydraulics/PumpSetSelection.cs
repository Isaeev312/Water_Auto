using WaterAuto.Core;

namespace WaterAuto.Calc.Hydraulics;

/// <summary>Подобранная установка, напор по её кривой при требуемой подаче, м, и замечания.</summary>
public sealed record PumpSetChoice(PumpSet? Chosen, double HeadAtDuty, IReadOnlyList<Issue> Issues);

/// <summary>
/// Подбор комплектной насосной установки по требуемым подаче и напору: основные насосы работают параллельно,
/// каждый — на Q / число основных; установка подходит, если подача в пределах кривой и напор по кривой не меньше
/// требуемого. Из подходящих — наименьшая мощность, затем наименьший избыток напора.
/// </summary>
public static class PumpSetSelection
{
    public static PumpSetChoice Choose(IReadOnlyList<PumpSet> sets, double qM3h, double hM)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var fits = sets
            .Select(s => (Set: s, H: HeadAt(s, qM3h)))
            .Where(x => x.H is { } h && h >= hM)
            .OrderBy(x => x.Set.PowerKw * x.Set.MainPumps)
            .ThenBy(x => x.H)
            .ToList();
        if (fits.Count == 0)
        {
            return new PumpSetChoice(null, 0, [new Issue("pump.noSet", IssueLevel.Error, $"В каталоге нет установки на Q = {qM3h:0.0} м³/ч и H = {hM:0.0} м: подберите насосы вручную.")]);
        }

        return new PumpSetChoice(fits[0].Set, fits[0].H!.Value, []);
    }

    /// <summary>Напор установки при подаче Q, м (по кривой одного насоса при Q / число основных); null — за пределами кривой.</summary>
    public static double? HeadAt(PumpSet set, double qM3h)
    {
        ArgumentNullException.ThrowIfNull(set);
        var q = qM3h / set.MainPumps;
        var c = set.PumpCurve.OrderBy(p => p.QM3h).ToList();
        for (var i = 1; i < c.Count; i++)
        {
            if (q >= c[i - 1].QM3h && q <= c[i].QM3h)
            {
                var span = c[i].QM3h - c[i - 1].QM3h;
                return span <= 0 ? c[i].HM : c[i - 1].HM + ((c[i].HM - c[i - 1].HM) * (q - c[i - 1].QM3h) / span);
            }
        }

        return null;
    }
}
