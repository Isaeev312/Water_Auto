using System.Globalization;
using WaterAuto.Core;

namespace WaterAuto.Calc.Routing;

/// <summary>Соединения по DN: вид и замечания по нормам.</summary>
public sealed record PipeJointsResult(IReadOnlyDictionary<int, JointKind> ByDn, IReadOnlyList<string> NormIds, IReadOnlyList<Issue> Issues);

/// <summary>
/// Вид соединения для каждого DN сети. Выбор проектировщика — сварка, разъёмные муфты или резьба; при муфтах малые DN (ниже порога
/// проектировщика) — на резьбе. Резьба и муфты допускаются только для труб по стандартам из п. 6.7.2.1, муфты — до DN из п. 6.7.2.6;
/// остальные DN — на сварке с замечанием. Категорию помещений (п. 6.7.2.5) и скрытую прокладку (п. 6.7.1.8) программа не знает —
/// напоминание проектировщику.
/// </summary>
public static class PipeJoints
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <param name="standardByDn">Стандарт труб каждого DN сети («ГОСТ 3262-75» и т. п.).</param>
    /// <param name="noGroovedGrade">DN, для которых в данных нет труб допустимого под муфты и резьбу стандарта: сварка для них —
    /// единственный вариант, замечание — сведение, а не предупреждение.</param>
    public static PipeJointsResult Resolve(IReadOnlyDictionary<int, string> standardByDn, JointKind preferred, int groovedMinDn, INormsProvider norms, IReadOnlySet<int>? noGroovedGrade = null)
    {
        ArgumentNullException.ThrowIfNull(standardByDn);
        ArgumentNullException.ThrowIfNull(norms);
        var map = new Dictionary<int, JointKind>();
        var issues = new List<Issue>();
        var kinds = norms.Find("sp485.p6_7_2_1.joints")!;
        var limit = norms.Find("sp485.p6_7_2_6.couplings")!;
        var ids = new List<string> { kinds.Id };
        var allowed = kinds.Attributes["couplingsAllowed"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var maxDn = (int)limit.Values["maxDn"]!.Value;
        var badStandard = new SortedSet<int>();
        var noGrade = new SortedSet<int>();
        var tooLarge = new SortedSet<int>();
        foreach (var (dn, standard) in standardByDn.OrderBy(x => x.Key))
        {
            if (preferred == JointKind.Welded)
            {
                map[dn] = JointKind.Welded;
            }
            else if (!allowed.Any(a => standard.StartsWith(a, StringComparison.Ordinal)))
            {
                // Резьба и муфты (п. 6.7.2.1) — только для труб допустимых стандартов; остальные — на сварке.
                map[dn] = JointKind.Welded;
                (noGroovedGrade?.Contains(dn) == true ? noGrade : badStandard).Add(dn);
            }
            else if (preferred == JointKind.Threaded || dn < groovedMinDn)
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

        if (preferred == JointKind.Grooved)
        {
            ids.Add(limit.Id);
        }

        string List(IEnumerable<int> dns) => "DN" + string.Join(", DN", dns);
        if (badStandard.Count > 0)
        {
            issues.Add(new Issue("joints.standard", IssueLevel.Warning,
                string.Create(Ru, $"Трубы {List(badStandard)} по {string.Join(", ", badStandard.Select(d => standardByDn[d]).Distinct())} соединяются только сваркой и на фланцах — резьба и разъёмные муфты допускаются для труб по {string.Join(", ", allowed)}; приняты сварные соединения, сменить сортамент или вид соединения."),
                Clause: kinds.Clause));
        }

        if (noGrade.Count > 0)
        {
            issues.Add(new Issue("joints.welded", IssueLevel.Info,
                string.Create(Ru, $"{List(noGrade)} — на сварке: в сортаменте данных нет труб этого размера по {string.Join(", ", allowed)}."),
                Clause: kinds.Clause));
        }

        if (tooLarge.Count > 0)
        {
            issues.Add(new Issue("joints.maxDn", IssueLevel.Warning,
                string.Create(Ru, $"Разъёмные муфты — не более DN{maxDn}; {List(tooLarge)} — на сварке."),
                Clause: limit.Clause));
        }

        // Напоминание — для любой сети: в помещениях А и Б недопустима именно сварка, за несъёмными потолками — разъёмные соединения.
        var ab = norms.Find("sp485.p6_7_2_5.categoryAB");
        var concealed = norms.Find("sp485.p6_7_1_8.concealed");
        if (ab is not null && concealed is not null)
        {
            var detachable = map.Where(x => x.Value != JointKind.Welded).Select(x => x.Key).Order().ToList();
            var accepted = detachable.Count == 0 ? "Сеть принята на сварке" : $"Разъёмные соединения приняты для {List(detachable)}";
            issues.Add(new Issue("joints.check", IssueLevel.Info,
                $"{accepted} — проверить по помещениям: {ab.Attributes["subject"]} — {ab.Attributes["rule"]} (п. {ab.Clause}); {concealed.Attributes["subject"]} — {concealed.Attributes["rule"]} (п. {concealed.Clause})."));
            ids.AddRange([ab.Id, concealed.Id]);
        }

        return new PipeJointsResult(map, ids, issues);
    }

}
