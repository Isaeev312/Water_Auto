using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>Соединения по DN: вид и замечания по нормам.</summary>
public sealed record PipeJointsResult(IReadOnlyDictionary<int, JointKind> ByDn, IReadOnlyList<string> NormIds, IReadOnlyList<Issue> Issues);

/// <summary>
/// Вид соединения для каждого DN сети. Выбор проектировщика — сварка или разъёмные муфты; при муфтах малые DN (ниже порога
/// проектировщика) — на резьбе. Муфты допускаются только для труб по стандартам из п. 6.7.2.1 и до DN из п. 6.7.2.6;
/// остальные DN — на сварке с замечанием.
/// </summary>
public static class PipeJoints
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <param name="standardByDn">Стандарт труб каждого DN сети («ГОСТ 3262-75» и т. п.).</param>
    public static PipeJointsResult Resolve(IReadOnlyDictionary<int, string> standardByDn, JointKind preferred, int groovedMinDn, INormsProvider norms)
    {
        ArgumentNullException.ThrowIfNull(standardByDn);
        ArgumentNullException.ThrowIfNull(norms);
        var map = new Dictionary<int, JointKind>();
        var issues = new List<Issue>();
        if (preferred != JointKind.Grooved)
        {
            foreach (var dn in standardByDn.Keys)
            {
                map[dn] = preferred;
            }

            return new PipeJointsResult(map, [], issues);
        }

        var kinds = norms.Find("sp485.p6_7_2_1.joints")!;
        var limit = norms.Find("sp485.p6_7_2_6.couplings")!;
        var allowed = kinds.Attributes["couplingsAllowed"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var maxDn = (int)limit.Values["maxDn"]!.Value;
        var badStandard = new SortedSet<int>();
        var tooLarge = new SortedSet<int>();
        foreach (var (dn, standard) in standardByDn.OrderBy(x => x.Key))
        {
            // Резьба и муфты (п. 6.7.2.1) — только для труб допустимых стандартов; остальные — на сварке.
            if (!allowed.Any(a => standard.StartsWith(a, StringComparison.Ordinal)))
            {
                map[dn] = JointKind.Welded;
                badStandard.Add(dn);
            }
            else if (dn < groovedMinDn)
            {
                map[dn] = JointKind.Threaded;
            }
            else if (dn > maxDn)
            {
                map[dn] = JointKind.Welded;
                tooLarge.Add(dn);
            }
            else
            {
                map[dn] = JointKind.Grooved;
            }
        }

        if (badStandard.Count > 0)
        {
            issues.Add(new Issue("joints.standard", IssueLevel.Warning,
                string.Create(Ru, $"Трубы DN{string.Join(", DN", badStandard)} по {string.Join(", ", badStandard.Select(d => standardByDn[d]).Distinct())} соединяются только сваркой и на фланцах — резьба и разъёмные муфты допускаются для труб по {string.Join(", ", allowed)}; приняты сварные соединения, сменить сортамент или вид соединения."),
                Clause: "6.7.2.1"));
        }

        if (tooLarge.Count > 0)
        {
            issues.Add(new Issue("joints.maxDn", IssueLevel.Warning,
                string.Create(Ru, $"Разъёмные муфты — не более DN{maxDn}; DN{string.Join(", DN", tooLarge)} — на сварке."),
                Clause: "6.7.2.6"));
        }

        // Категорию помещений и скрытую прокладку программа не знает — напоминание проектировщику со ссылками на пункты.
        var ab = norms.Find("sp485.p6_7_2_5.categoryAB");
        var concealed = norms.Find("sp485.p6_7_1_8.concealed");
        var ids = new List<string> { kinds.Id, limit.Id };
        if (ab is not null && concealed is not null && map.Values.Any(k => k != JointKind.Welded))
        {
            issues.Add(new Issue("joints.check", IssueLevel.Info,
                $"Разъёмные соединения приняты для всей сети — проверить: в помещениях категорий {ab.Attributes["categories"]} допускаются только резьбовые и фланцевые соединения (п. {ab.Clause}); за несъёмными подвесными потолками и в закрытых штробах — как правило, сварка, разъёмные — при обосновании и с ревизионными люками (п. {concealed.Clause})."));
            ids.AddRange([ab.Id, concealed.Id]);
        }

        return new PipeJointsResult(map, ids, issues);
    }
}
