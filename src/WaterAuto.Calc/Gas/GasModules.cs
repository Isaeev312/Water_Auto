using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Gas;

/// <summary>
/// Число модулей АУГП для помещения: Mг по Д.1 включает остаток Mб·n, поэтому n подбирается последовательно до n ≥ Mг/Mмакс,
/// Mмакс = вместимость × коэффициент наполнения по ТД. Наполнение не меньше минимального по 9.6.5 (данные норм).
/// </summary>
public static class GasModules
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static GasModulesResult Select(GasRoom room, GasModuleModel module, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        var key = room.AgentId[(room.AgentId.LastIndexOf('.') + 1)..];
        if (!module.Charges.TryGetValue(key, out var charge) || charge.MaxFillKgPerL <= 0)
        {
            var mass = GasMass.Calculate(room, norms);
            issues.Add(new Issue("gas.module.agent", IssueLevel.Error, $"«{room.Name}»: модуль {module.Name} по ТД не заправляется выбранным ГОТВ — выбрать другой модуль.", room.Id));
            return new(0, 0, 0, mass, [.. mass.Issues, .. issues]);
        }

        if (charge.ResidueKg is null)
        {
            issues.Add(new Issue("gas.module.noResidue", IssueLevel.Warning, $"«{room.Name}»: остаток ГОТВ в модуле {module.Name} в ТД не указан — Mб принят 0, уточнить у изготовителя.", room.Id));
        }

        var maxMass = module.VolumeL * charge.MaxFillKgPerL;
        var n = 1;
        GasMassResult result;
        while (true)
        {
            result = GasMass.Calculate(room with { Modules = n, ModuleResidueKg = charge.ResidueKg ?? 0 }, norms);
            var need = result.TotalMass > 0 ? (int)Math.Ceiling((result.TotalMass / maxMass) - 1e-9) : 0;
            if (need <= n || need == 0)
            {
                n = result.TotalMass > 0 ? n : 0;
                break;
            }

            n = need;
        }

        var perModule = n > 0 ? result.TotalMass / n : 0;
        var fill = maxMass > 0 ? perModule / maxMass * 100 : 0;
        if (n > 0 && norms.Find("sp485.p9_6_5.minFill") is { } minFill && fill < minFill.Values["minFillPercent"]!.Value - 1e-9)
        {
            issues.Add(new Issue("gas.module.fill", IssueLevel.Error,
                string.Create(Ru, $"«{room.Name}»: наполнение {module.Name} {perModule:0.#} кг — {fill:0} % от максимального {maxMass:0.#} кг, меньше {minFill.Values["minFillPercent"]:0} % (п. 9.6.5) — взять модуль меньшей вместимости."),
                room.Id, "СП 485.1311500.2020, п. 9.6.5"));
        }

        return new(n, perModule, fill, result, [.. result.Issues, .. issues]);
    }
}
