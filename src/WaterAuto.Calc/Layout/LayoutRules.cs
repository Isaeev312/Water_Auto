using WaterAuto.Core;

namespace WaterAuto.Calc.Layout;

/// <summary>
/// Правила размещения оросителей для одного помещения — из табл. 6.1 и пп. 6.2.10, 6.2.14, 6.2.21.
/// </summary>
/// <param name="MaxSpacing">Наибольшее расстояние между оросителями (табл. 6.1 или строже — профиль заказчика).</param>
/// <param name="MinSpacing">Наименьшее расстояние между оросителями (6.2.21).</param>
/// <param name="MaxWallDistance">Наибольшее расстояние от оросителя до стены (6.2.21).</param>
/// <param name="BeamHeightLimit">Балки выше этого значения делят перекрытие на поля (6.2.10).</param>
/// <param name="DuctMinWidth">Короба шире этого значения требуют оросителей под собой (6.2.14).</param>
/// <param name="NormIds">ID записей норм, из которых взяты значения, — для журнала расчёта.</param>
public sealed record LayoutRules(
    Length MaxSpacing,
    Length MinSpacing,
    Length MaxWallDistance,
    Length BeamHeightLimit,
    Length DuctMinWidth,
    IReadOnlyList<string> NormIds)
{
    /// <summary>
    /// Правила для группы помещений и класса конструкций. <paramref name="maxSpacing"/> — шаг строже табл. 6.1
    /// (профиль заказчика); ослабить норму нельзя — это замечание-ошибка.
    /// </summary>
    public static LayoutRulesResult Resolve(INormsProvider norms, string group, ConstructionClass construction, Length? maxSpacing = null)
    {
        ArgumentNullException.ThrowIfNull(norms);
        var issues = new List<Issue>();
        var table = norms.ByClause("табл. 6.1").FirstOrDefault(r => r.Attributes.GetValueOrDefault("group") == group);
        var tableSpacing = table?.Values.GetValueOrDefault("maxSpacing");
        if (table is null || tableSpacing is null)
        {
            issues.Add(new Issue("layout.noSpacing", IssueLevel.Error, $"Для группы помещений «{group}» в табл. 6.1 нет расстояния между оросителями.", Clause: "табл. 6.1"));
            return new LayoutRulesResult(null, issues);
        }

        var spacing = tableSpacing.Value;
        if (maxSpacing is { } tightened)
        {
            if (tightened.Meters > spacing + 1e-9)
            {
                issues.Add(new Issue(
                    "layout.spacingLooser",
                    IssueLevel.Error,
                    $"Шаг {tightened} больше допустимого по табл. 6.1 ({new Length(spacing)}): профиль может только ужесточать норму.",
                    Clause: "табл. 6.1"));
                return new LayoutRulesResult(null, issues);
            }

            spacing = tightened.Meters;
        }

        var label = Label(construction);
        var wall = ForClass(norms, "6.2.21", label);
        var beam = ForClass(norms, "6.2.10", label);
        var min = norms.Find("sp485.p6_2_21.minSpacing");
        var duct = norms.Find("sp485.p6_2_14.duct");
        if (wall is null || beam is null || min is null || duct is null)
        {
            issues.Add(new Issue("layout.noRules", IssueLevel.Error, $"В данных норм нет правил пп. 6.2.10, 6.2.14, 6.2.21 для класса {label}.", Clause: "6.2"));
            return new LayoutRulesResult(null, issues);
        }

        // К0/К1: предел — доля шага табл. 6.1. Берётся доля фактического шага: при шаге строже нормы
        // это строже буквального прочтения, при табличном шаге совпадает с ним (open_questions.md В-16).
        var wallMax = wall.Values.TryGetValue("ratioToTableSpacing", out var ratio)
            ? ratio!.Value * spacing
            : wall.Values["wallMax"]!.Value;

        var rules = new LayoutRules(
            new Length(spacing),
            new Length(min.Values["minSpacing"]!.Value),
            new Length(wallMax),
            new Length(beam.Values["beamHeight"]!.Value),
            new Length(duct.Values["minWidth"]!.Value),
            [table.Id, wall.Id, min.Id, beam.Id, duct.Id]);
        return new LayoutRulesResult(rules, issues);
    }

    private static NormRecord? ForClass(INormsProvider norms, string clause, string label) =>
        norms.ByClause(clause).FirstOrDefault(r =>
            r.Attributes.GetValueOrDefault("constructionClasses")?.Split(',').Contains(label) == true);

    // Обозначения классов в данных норм — как в тексте СП, кириллицей.
    private static string Label(ConstructionClass c) => c switch
    {
        ConstructionClass.K0 => "К0",
        ConstructionClass.K1 => "К1",
        ConstructionClass.K2 => "К2",
        ConstructionClass.K3 => "К3",
        _ => "ненормируемый",
    };
}

public sealed record LayoutRulesResult(LayoutRules? Rules, IReadOnlyList<Issue> Issues);
