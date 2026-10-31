using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>Точки управления и замечания этапа.</summary>
public sealed record ControlPointsResult(IReadOnlyList<ControlPoint> Points, IReadOnlyList<Issue> Issues);

/// <summary>
/// Этап «Точки управления» (ADR 0012, п. 2): источники сигналов АУПТ раскрываются в точки — по одной на устройство.
/// Обозначение точки — роль позиции и номер устройства в ней: у источника с ролью оно не зависит от других позиций и не
/// меняется, пока не меняется количество устройств этой позиции. У источника без роли вместо неё — вид сигнала и порядковый
/// номер источника этого вида, поэтому такое обозначение зависит от порядка источников. Если у роли несколько источников
/// (одно устройство с несколькими сигналами, например шкаф насосов), к роли добавляется вид сигнала.
/// Обозначение для перечня — буквенная часть из каталога и сквозной номер в её серии в порядке источников (ЗД1, ЗД2…);
/// единственное устройство серии — без номера, кроме устройств секций (УУ1 и при одной секции); сигналы одного устройства делят обозначение. Номер секции — из
/// <see cref="SignalSource.PerSection"/>: устройства позиции идут по секциям подряд, как ветви гребёнки на схеме.
/// </summary>
public static class ControlPoints
{
    public static ControlPointsResult Build(IEnumerable<SignalSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.ToList();
        var shared = list.Where(s => s.Role is not null).GroupBy(s => s.Role!).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var points = new List<ControlPoint>();
        var issues = new List<Issue>();
        var unnamed = new Dictionary<SignalKind, int>();
        var designations = Designations(list);
        foreach (var s in list)
        {
            var key = s.Role is { } role && shared.Contains(role) ? $"{role}.{Code(s.Kind)}" : s.Role;
            if (key is null)
            {
                unnamed[s.Kind] = unnamed.GetValueOrDefault(s.Kind) + 1;
                key = $"{Code(s.Kind)}{unnamed[s.Kind]}";
            }

            for (var n = 1; n <= s.Count; n++)
            {
                var id = $"{key}.{n}";
                var designation = s.Role is not null && designations.TryGetValue((s.Role, n), out var d) ? d : id;
                int? section = s.PerSection > 0 ? ((n - 1) / s.PerSection) + 1 : null;
                points.Add(new ControlPoint(id, s.Equipment, s.Kind, s.Clause, s.Role, designation, section));
            }

            if (s.Count > 0 && s.Clause is null)
            {
                issues.Add(new Issue("aps.signal.noClause", IssueLevel.Info,
                    $"Сигнал «{s.Equipment}» включён в перечень по составу оборудования; пункт нормы, требующий контроля, в каталоге не указан.",
                    key));
            }
        }

        return new ControlPointsResult(points, issues);
    }

    /// <summary>Обозначения устройств (роль, номер) → «ЗД3»: устройство считается один раз, сколько бы сигналов у него ни было.</summary>
    private static Dictionary<(string Role, int N), string> Designations(List<SignalSource> sources)
    {
        var devices = sources
            .Where(s => s.Role is not null && s.Mark is not null)
            .DistinctBy(s => s.Role)
            .SelectMany(s => Enumerable.Range(1, s.Count).Select(n => (Role: s.Role!, N: n, Mark: s.Mark!, Sectioned: s.PerSection > 0)))
            .ToList();
        var total = devices.GroupBy(d => d.Mark).ToDictionary(g => g.Key, g => g.Count());
        var next = new Dictionary<string, int>();
        var result = new Dictionary<(string, int), string>();
        foreach (var (role, n, mark, sectioned) in devices)
        {
            next[mark] = next.GetValueOrDefault(mark) + 1;
            result[(role, n)] = total[mark] == 1 && !sectioned ? mark : $"{mark}{next[mark]}";
        }

        return result;
    }

    private static string Code(SignalKind kind) => kind switch
    {
        SignalKind.ValvePosition => "valvePosition",
        SignalKind.PressureSwitch => "pressureSwitch",
        SignalKind.PumpStart => "pumpStart",
        SignalKind.PumpFault => "pumpFault",
        SignalKind.AutomaticsOff => "automaticsOff",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
