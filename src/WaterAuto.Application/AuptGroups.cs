using System.Globalization;
using WaterAuto.Calc.Hydraulics;
using WaterAuto.Calc.Layout;
using WaterAuto.Calc.Routing;
using WaterAuto.Core;

namespace WaterAuto.Application;

/// <summary>Помещения одной группы (с одинаковыми высотами): правила раскладки и параметры по таблицам СП 485.</summary>
public sealed record AuptGroupZone(string Group, LayoutRules Rules, FireParameters Parameters, IReadOnlySet<string> RoomIds);

/// <summary>Раскладка и трассы по группам помещений: каждая группа — по своим правилам; результаты объединены (ID помещений уникальны).</summary>
public sealed record AuptGroupsResult(IReadOnlyList<AuptGroupZone> Zones, SprinklerLayout Layout, RoutingResult Routing)
{
    public AuptGroupZone ZoneOf(string roomId) => Zones.First(z => z.RoomIds.Contains(roomId));

    /// <summary>Объединение результатов этажей (ID помещений этажей различаются префиксом).</summary>
    public static AuptGroupsResult Merge(IReadOnlyList<AuptGroupsResult> parts) => new(
        [.. parts.SelectMany(p => p.Zones)],
        new SprinklerLayout([.. parts.SelectMany(p => p.Layout.Sprinklers)], [.. parts.SelectMany(p => p.Layout.Zones)], [.. parts.SelectMany(p => p.Layout.Issues)]),
        new RoutingResult([.. parts.SelectMany(p => p.Routing.Routes)], [.. parts.SelectMany(p => p.Routing.Issues)]));
}

/// <summary>
/// Группы помещений в одном проекте (design.rooms): помещения без записи — группа design.roomGroup. Чертёж делится по помещениям,
/// колонны, балки, короба, зоны запрета и ввод остаются общими. Как считать установку на помещения разных групп, СП 485 прямо
/// не говорит (только для пены — по помещению с наибольшим расходом раствора); программа берёт огибающую — В-27.
/// </summary>
public static class AuptGroups
{
    public static AuptGroupsResult? Build(FloorPlan plan, ProjectDesign design, INormsProvider norms, List<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(issues);
        foreach (var r in design.Rooms.Where(r => !plan.Rooms.Any(x => string.Equals(x.Name, r.Room, StringComparison.OrdinalIgnoreCase))))
        {
            issues.Add(new Issue("design.rooms.noRoom", IssueLevel.Warning, $"design.rooms: помещение «{r.Room}» на чертеже не найдено (слой ПТ_КОНТУР) — запись не применена."));
        }

        ProjectRoomGroup? Entry(Room room) => design.Rooms.FirstOrDefault(r => string.Equals(r.Room, room.Name, StringComparison.OrdinalIgnoreCase));
        var keyed = plan.Rooms.GroupBy(room =>
        {
            var e = Entry(room);
            return (Group: e?.Group ?? design.RoomGroup, RoomHeight: e?.RoomHeight ?? design.RoomHeight, StorageHeight: e is null ? design.StorageHeight : e.StorageHeight);
        }).ToList();

        var zones = new List<AuptGroupZone>();
        var sprinklers = new List<PlacedSprinkler>();
        var layoutZones = new List<LayoutZone>();
        var layoutIssues = new List<Issue>();
        var routes = new List<PipeRoute>();
        var routeIssues = new List<Issue>();
        foreach (var g in keyed)
        {
            var resolved = LayoutRules.Resolve(norms, g.Key.Group, design.ConstructionClass, design.MaxSpacing is { } m ? new Length(m) : null);
            issues.AddRange(resolved.Issues);
            var fire = FireParameters.Resolve(norms, g.Key.Group, g.Key.RoomHeight is { } rh ? new Length(rh) : null, g.Key.StorageHeight is { } sh ? new Length(sh) : null);
            issues.AddRange(fire.Issues);
            if (resolved.Rules is not { } rules || fire.Parameters is not { } parameters)
            {
                return null;
            }

            var sub = plan with { Rooms = [.. g] };
            var layout = SprinklerLayouter.Layout(sub, rules, design.FloorElevation is { } floor ? new Length(floor) : null);
            var routing = PipeRouter.Route(sub, layout, norms, rules, Pressure.Zero, design.Ring);
            sprinklers.AddRange(layout.Sprinklers);
            layoutZones.AddRange(layout.Zones);
            layoutIssues.AddRange(layout.Issues);
            routes.AddRange(routing.Routes);
            routeIssues.AddRange(routing.Issues);
            zones.Add(new AuptGroupZone(g.Key.Group, rules, parameters, g.Select(x => x.Id).ToHashSet(StringComparer.Ordinal)));
        }

        return new AuptGroupsResult(zones, new SprinklerLayout(sprinklers, layoutZones, layoutIssues), new RoutingResult(routes, routeIssues));
    }

    /// <summary>
    /// Огибающая расчётов групп: насос — наибольший расход и наибольший напор, бак — наибольший объём воды (В-27). Одна группа —
    /// расчёт с наибольшим давлением насоса без изменений.
    /// </summary>
    public static DesignResult Envelope(IReadOnlyList<DesignResult> designs, AuptGroupsResult groups, List<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(designs);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(issues);
        var worst = designs.MaxBy(d => d.PumpPressure.Mpa)!;
        if (groups.Zones.Select(z => z.Group).Distinct(StringComparer.Ordinal).Count() < 2)
        {
            return worst;
        }

        var maxFlow = designs.MaxBy(d => d.PumpFlow.LitersPerSecond)!;
        var maxWater = designs.MaxBy(d => d.WaterVolume)!;
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var rows = groups.Zones.Select(z =>
        {
            var d = designs.Where(x => z.RoomIds.Contains(x.Route.RoomId)).MaxBy(x => x.PumpPressure.Mpa);
            return d is null ? $"группа {z.Group} — нет расчёта" : string.Create(ru, $"группа {z.Group} ({z.Parameters.Source}) — Q = {d.PumpFlow.LitersPerSecond:0.##} л/с, H = {d.PumpHead.Meters:0.#} м, W = {d.WaterVolume:0.#} м³");
        });
        issues.Add(new Issue("design.groups.envelope", IssueLevel.Info,
            string.Create(ru, $"Помещения разных групп: {string.Join("; ", rows)}. Насос — по наибольшему расходу {maxFlow.PumpFlow.LitersPerSecond:0.##} л/с и наибольшему напору {worst.PumpHead.Meters:0.#} м, запас воды — {maxWater.WaterVolume:0.#} м³ (прочтение программы, В-27).")));
        return worst with { PumpFlow = maxFlow.PumpFlow, WaterVolume = maxWater.WaterVolume, TankVolume = Math.Max(worst.TankVolume, maxWater.TankVolume), TankMaxFill = Math.Max(worst.TankMaxFill, maxWater.TankMaxFill) };
    }
}
