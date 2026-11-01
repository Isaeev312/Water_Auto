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
            if (dn < groovedMinDn)
            {
                map[dn] = JointKind.Threaded;
            }
            else if (dn > maxDn)
            {
                map[dn] = JointKind.Welded;
                tooLarge.Add(dn);
            }
            else if (!allowed.Any(a => standard.StartsWith(a, StringComparison.Ordinal)))
            {
                map[dn] = JointKind.Welded;
                badStandard.Add(dn);
            }
            else
            {
                map[dn] = JointKind.Grooved;
            }
        }

        if (badStandard.Count > 0)
        {
            issues.Add(new Issue("joints.standard", IssueLevel.Error,
                string.Create(Ru, $"Трубы DN{string.Join(", DN", badStandard)} по {string.Join(", ", badStandard.Select(d => standardByDn[d]).Distinct())} соединяются только сваркой и на фланцах — разъёмные муфты допускаются для труб по {string.Join(", ", allowed)}; приняты сварные соединения, сменить сортамент или вид соединения."),
                Clause: "6.7.2.1"));
        }

        if (tooLarge.Count > 0)
        {
            issues.Add(new Issue("joints.maxDn", IssueLevel.Warning,
                string.Create(Ru, $"Разъёмные муфты — не более DN{maxDn}; DN{string.Join(", DN", tooLarge)} — на сварке."),
                Clause: "6.7.2.6"));
        }

        return new PipeJointsResult(map, [kinds.Id, limit.Id], issues);
    }
}
