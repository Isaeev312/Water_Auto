using WaterAuto.Core;

namespace WaterAuto.Alarm;

/// <summary>Точки управления и замечания этапа.</summary>
public sealed record ControlPointsResult(IReadOnlyList<ControlPoint> Points, IReadOnlyList<Issue> Issues);

/// <summary>
/// Этап «Точки управления» (ADR 0012, п. 2): источники сигналов АУПТ раскрываются в точки — по одной на устройство.
/// Обозначение точки — роль позиции и номер устройства в ней: у источника с ролью оно не зависит от других позиций и не
/// меняется, пока не меняется количество устройств этой позиции. У источника без роли вместо неё — вид сигнала и порядковый
/// номер источника этого вида, поэтому такое обозначение зависит от порядка источников.
/// </summary>
public static class ControlPoints
{
    public static ControlPointsResult Build(IEnumerable<SignalSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var points = new List<ControlPoint>();
        var issues = new List<Issue>();
        var unnamed = new Dictionary<SignalKind, int>();
        foreach (var s in sources)
        {
            var key = s.Role;
            if (key is null)
            {
                unnamed[s.Kind] = unnamed.GetValueOrDefault(s.Kind) + 1;
                key = $"{Code(s.Kind)}{unnamed[s.Kind]}";
            }

            for (var n = 1; n <= s.Count; n++)
            {
                points.Add(new ControlPoint($"{key}.{n}", s.Equipment, s.Kind, s.Clause));
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

    private static string Code(SignalKind kind) => kind switch
    {
        SignalKind.ValvePosition => "valvePosition",
        SignalKind.PressureSwitch => "pressureSwitch",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
