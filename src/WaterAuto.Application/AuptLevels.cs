using System.Globalization;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Core;
using WaterAuto.Import;

namespace WaterAuto.Application;

/// <summary>Этаж АУПТ: имя, превышение пола над основным этажом, м, план (ID с префиксом) и раскладка с трассами по группам.</summary>
public sealed record AuptLevel(int Index, string Name, double Delta, string Prefix, FloorPlan Plan, AuptGroupsResult Groups, int? RiserDn = null, double RiserRun = 0);

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
            // Отметка пола этажа — для условия п. 6.2.14 (короба по высоте от пола).
            var levelDesign = design with { FloorElevation = design.FloorElevation is null ? null : l.Elevation };
            if (AuptGroups.Build(tagged, levelDesign, norms, issues) is not { } groups)
            {
                continue;
            }

            if (l.RiserDn is null)
            {
                issues.Add(new Issue("design.level.noRiserDn", IssueLevel.Warning,
                    $"Этаж «{name}»: DN стояка не задан (design.levels[].riserDn) — стояк посчитан по DN подводящего {design.SupplyDn}; при меньшем стояке потери занижены (В-28)."));
            }

            levels.Add(new AuptLevel(k, name, l.Elevation - baseFloor, prefix, tagged, groups, l.RiserDn, l.RiserRun));
        }

        return levels;
    }

    public static AuptLevel? Of(string roomId, IReadOnlyList<AuptLevel> levels) =>
        levels.FirstOrDefault(l => roomId.StartsWith(l.Prefix, StringComparison.Ordinal));

    /// <summary>Параметры расчёта трассы этажа: диктующий выше на Δz, подводящий длиннее на стояк |Δz|; основной этаж — без изменений.</summary>
    /// <remarks>Свой подвод секции (<paramref name="feed"/>) заменяет общие длину и DN подводящего; стояк этажа добавляется к нему.</remarks>
    public static DesignSettings SettingsFor(string roomId, IReadOnlyList<AuptLevel> levels, DesignSettings settings, ProjectDesign design, ProjectFeed? feed = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(design);
        var supply = feed?.SupplyLength ?? design.SupplyLength;
        if (feed is not null)
        {
            settings = settings with { SupplyLength = new Length(supply), SupplyDn = feed.SupplyDn ?? settings.SupplyDn };
        }

        // Стояк своим DN — отдельный участок |Δz|, разводка до него — по DN подводящего; без DN — всё по подводящему (В-28).
        return Of(roomId, levels) is { } l
            ? l.RiserDn is { } dn
                ? settings with
                {
                    DictatingAboveAxis = new Length(design.DictatingAboveAxis + l.Delta),
                    SupplyLength = new Length(supply + l.RiserRun),
                    RiserLength = new Length(Math.Abs(l.Delta)),
                    RiserDn = dn,
                }
                : settings with
                {
                    DictatingAboveAxis = new Length(design.DictatingAboveAxis + l.Delta),
                    SupplyLength = new Length(supply + l.RiserRun + Math.Abs(l.Delta)),
                }
            : settings;
    }
}
