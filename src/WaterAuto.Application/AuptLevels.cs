using System.Globalization;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Import;

namespace WaterAuto.Application;

/// <summary>Этаж АУПТ: имя, превышение пола над основным этажом, м, план (ID с префиксом) и раскладка с трассами по группам.</summary>
public sealed record AuptLevel(int Index, string Name, double Delta, string Prefix, FloorPlan Plan, AuptGroupsResult Groups);

/// <summary>
/// Этажи сети АУПТ (design.levels). Каждый этаж — свой чертёж; ввод этажа (слой ПТ_ВВОД) — место стояка. Сеть этажа считается от
/// ввода как основная; подводящий трубопровод этажа длиннее на стояк |Δz|, диктующий выше оси насоса на Δz. Диктующий этаж — с
/// наибольшим требуемым давлением насоса (не обязательно верхний: группа и сеть этажа важнее высоты).
/// </summary>
public static class AuptLevels
{
    public static IReadOnlyList<AuptLevel> Build(ProjectFile project, string projectPath, ProjectDesign design, INormsProvider norms, List<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(issues);
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var baseFloor = design.FloorElevation ?? 0;
        var levels = new List<AuptLevel>();
        foreach (var (l, k) in design.Levels.Select((l, k) => (l, k + 1)))
        {
            var name = l.Name.Length > 0 ? l.Name : string.Create(CultureInfo.InvariantCulture, $"этаж {k + 1}");
            var import = FloorPlanImporter.Import(Path.GetFullPath(Path.Combine(dir, l.Drawing)));
            issues.AddRange(import.Issues.Select(x => x with { Message = $"Этаж «{name}»: {x.Message}" }));
            if (import.Plan is not { } plan)
            {
                issues.Add(new Issue("design.level.noDrawing", IssueLevel.Error, $"АУПТ: чертёж этажа «{name}» ({l.Drawing}) не прочитан — этаж не рассчитан."));
                continue;
            }

            var prefix = string.Create(CultureInfo.InvariantCulture, $"L{k}.");
            var tagged = plan with
            {
                Rooms = [.. plan.Rooms.Select(r => r with { Id = prefix + r.Id })],
                Inlets = [.. plan.Inlets.Select(i => i with { Id = prefix + i.Id })],
                Ducts = [.. plan.Ducts.Select(d => d with { Id = prefix + d.Id })],
                PumpRooms = [],
            };
            if (AuptGroups.Build(tagged, design, norms, issues) is not { } groups)
            {
                continue;
            }

            levels.Add(new AuptLevel(k, name, l.Elevation - baseFloor, prefix, tagged, groups));
        }

        return levels;
    }

    public static AuptLevel? Of(string roomId, IReadOnlyList<AuptLevel> levels) =>
        levels.FirstOrDefault(l => roomId.StartsWith(l.Prefix, StringComparison.Ordinal));

    /// <summary>Параметры расчёта трассы этажа: диктующий выше на Δz, подводящий длиннее на стояк |Δz|; основной этаж — без изменений.</summary>
    public static DesignSettings SettingsFor(string roomId, IReadOnlyList<AuptLevel> levels, DesignSettings settings, ProjectDesign design)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(design);
        return Of(roomId, levels) is { } l
            ? settings with
            {
                DictatingAboveAxis = new Length(design.DictatingAboveAxis + l.Delta),
                SupplyLength = new Length(design.SupplyLength + Math.Abs(l.Delta)),
            }
            : settings;
    }
}
