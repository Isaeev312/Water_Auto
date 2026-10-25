using WaterAuto.Calc.Layout;
using WaterAuto.Core;
using WaterAuto.Norms;

namespace WaterAuto.Calc.Tests;

public class SprinklerLayoutTests
{
    private static readonly JsonNormsProvider Norms = JsonNormsProvider.Load(RepoPaths.Norms);

    private static LayoutRules Rules(ConstructionClass c = ConstructionClass.K0, double? spacing = null) =>
        LayoutRules.Resolve(Norms, "2", c, spacing is null ? null : new Length(spacing.Value)).Rules!;

    private static Polygon Poly(params (double X, double Y)[] v) => new(v.Select(p => new PlanPoint(p.X, p.Y)).ToList());

    private static Polygon Rect(double x0, double y0, double x1, double y1) => Poly((x0, y0), (x1, y0), (x1, y1), (x0, y1));

    private static FloorPlan Plan(Polygon contour, IEnumerable<Column>? columns = null, IEnumerable<NoSprinklerZone>? zones = null, IEnumerable<Beam>? beams = null, IEnumerable<Duct>? ducts = null) =>
        new([new Room("R1", "Автостоянка", contour)], [.. columns ?? []], [.. beams ?? []], [.. ducts ?? []], [.. zones ?? []], [new Inlet("I1", contour.Vertices[0], null)]);

    [Fact]
    public void Rules_come_from_table_6_1_and_clause_6_2_21()
    {
        var k0 = Rules();
        k0.MaxSpacing.Meters.ShouldBe(3.5);
        k0.MinSpacing.Meters.ShouldBe(1.5);
        k0.MaxWallDistance.Meters.ShouldBe(1.75);
        k0.BeamHeightLimit.Meters.ShouldBe(0.3);
        k0.DuctMinWidth.Meters.ShouldBe(0.75);
        k0.NormIds.ShouldContain("sp485.t6_1.group2");

        var k2 = Rules(ConstructionClass.K2);
        k2.MaxWallDistance.Meters.ShouldBe(1.2);
        k2.BeamHeightLimit.Meters.ShouldBe(0.2);
        Rules(ConstructionClass.Unrated).MaxWallDistance.Meters.ShouldBe(1.2);
    }

    [Fact]
    public void Tightened_spacing_halves_for_the_wall_limit_and_cannot_loosen_the_norm()
    {
        // Профиль Фонда реновации: шаг 3,0 м. Половина фактического шага строже буквального прочтения (В-16).
        Rules(spacing: 3.0).MaxWallDistance.Meters.ShouldBe(1.5);

        var looser = LayoutRules.Resolve(Norms, "2", ConstructionClass.K0, new Length(4.0));
        looser.Rules.ShouldBeNull();
        looser.Issues.ShouldHaveSingleItem().Code.ShouldBe("layout.spacingLooser");
    }

    [Fact]
    public void Unknown_group_is_an_issue()
    {
        var r = LayoutRules.Resolve(Norms, "99", ConstructionClass.K0);
        r.Rules.ShouldBeNull();
        r.Issues.ShouldHaveSingleItem().Code.ShouldBe("layout.noSpacing");
    }

    [Theory]
    [InlineData(ConstructionClass.K0, null, 54)] // 9 × 6: шаг ≈ 3,33 м, до стен ≈ 1,67 м
    [InlineData(ConstructionClass.K2, null, 63)] // до стен ≤ 1,2 м: 9 × 7
    [InlineData(ConstructionClass.K0, 3.0, 70)] // профиль 3,0 м: 10 × 7
    public void Rectangle_30_by_20_m(ConstructionClass c, double? spacing, int count)
    {
        var plan = Plan(Rect(0, 0, 30000, 20000));
        var rules = Rules(c, spacing);
        var layout = SprinklerLayouter.Layout(plan, rules);
        layout.Issues.ShouldBeEmpty();
        layout.Sprinklers.Count.ShouldBe(count);
        LayoutChecks.ShouldSatisfy(layout, plan, rules);
    }

    [Fact]
    public void Small_room_gets_one_sprinkler_in_the_middle()
    {
        var plan = Plan(Rect(0, 0, 2000, 2000));
        var layout = SprinklerLayouter.Layout(plan, Rules());
        layout.Sprinklers.ShouldHaveSingleItem().Location.ShouldBe(new PlanPoint(1000, 1000));
    }

    [Fact]
    public void Narrow_room_keeps_the_minimum_spacing()
    {
        // 2,5 м при пределе до стен 1,2 м: два ряда, между ними не меньше 1,5 м.
        var plan = Plan(Rect(0, 0, 2500, 10000));
        var rules = Rules(ConstructionClass.K2);
        var layout = SprinklerLayouter.Layout(plan, rules);
        layout.Issues.ShouldBeEmpty();
        layout.Sprinklers.Count.ShouldBe(8);
        LayoutChecks.ShouldSatisfy(layout, plan, rules);
    }

    [Fact]
    public void L_shaped_room_compares_cut_directions_and_keeps_rows_aligned_across_cuts()
    {
        var contour = Poly((0, 0), (30000, 0), (30000, 10000), (15000, 10000), (15000, 20000), (0, 20000));
        var plan = Plan(contour);
        var rules = Rules();
        var room = plan.Rooms[0];

        // Вертикальные разрезы: 15 × 20 (5 × 6) + 15 × 10 (5 × 3) = 45; горизонтальные: 30 × 10 (9 × 3) + 15 × 10 (5 × 3) = 42.
        SprinklerLayouter.LayoutRoom(room, plan, rules, CutDirection.Vertical).Sprinklers.Count.ShouldBe(45);
        SprinklerLayouter.LayoutRoom(room, plan, rules, CutDirection.Horizontal).Sprinklers.Count.ShouldBe(42);

        var layout = SprinklerLayouter.Layout(plan, rules);
        layout.Issues.ShouldBeEmpty();
        layout.Sprinklers.Count.ShouldBe(42);
        layout.Zones.Count.ShouldBe(2);
        LayoutChecks.ShouldSatisfy(layout, plan, rules);
    }

    public static TheoryData<string, (double X, double Y)[]> NarrowZones => new()
    {
        // Ниша 1000 × 300 мм в верхней стене: своей линии в нише нет, верхний ряд ставится не дальше 1,75 м от дна ниши.
        { "ниша", [(0, 0), (30000, 0), (30000, 20000), (15000, 20000), (15000, 20300), (14000, 20300), (14000, 20000), (0, 20000)] },
        // Ступенька 15 мм в верхней стене — неточность чертежа.
        { "ступенька сверху", [(0, 0), (30000, 0), (30000, 20015), (15000, 20015), (15000, 20000), (0, 20000)] },
        // Ступенька 15 мм в левой стене.
        { "ступенька слева", [(15, 0), (30000, 0), (30000, 20000), (0, 20000), (0, 10000), (15, 10000)] },
    };

    [Theory]
    [MemberData(nameof(NarrowZones))]
    public void Narrow_zones_are_served_by_their_neighbours(string name, (double X, double Y)[] vertices)
    {
        var plan = Plan(Poly(vertices));
        var rules = Rules();
        foreach (var cut in new[] { CutDirection.Vertical, CutDirection.Horizontal })
        {
            var room = SprinklerLayouter.LayoutRoom(plan.Rooms[0], plan, rules, cut);
            room.Issues.ShouldBeEmpty($"{name}, {cut}");
            LayoutChecks.ShouldSatisfy(new SprinklerLayout(room.Sprinklers, room.Zones, room.Issues), plan, rules);
        }

        SprinklerLayouter.Layout(plan, rules).Sprinklers.Count.ShouldBe(54, name);
    }

    [Fact]
    public void Sliver_between_two_walls_is_a_warning_with_location()
    {
        // Зона запрета не доходит до стены на 15 мм: щель между двумя стенами никто не обслуживает.
        var plan = Plan(Rect(0, 0, 30000, 20000), zones: [new NoSprinklerZone("Z1", Rect(5000, 5000, 10000, 19985))]);
        var layout = SprinklerLayouter.Layout(plan, Rules());
        var issue = layout.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("layout.sliver");
        issue.Level.ShouldBe(IssueLevel.Warning);
        issue.Location.ShouldNotBeNull().YMm.ShouldBe(19992.5, 1);
    }

    [Fact]
    public void Rotated_room_is_laid_out_along_its_walls()
    {
        var angle = Math.PI / 6;
        PlanPoint R(double x, double y) => new((x * Math.Cos(angle)) - (y * Math.Sin(angle)) + 5000, (x * Math.Sin(angle)) + (y * Math.Cos(angle)) - 3000);
        var contour = new Polygon([R(0, 0), R(30000, 0), R(30000, 20000), R(0, 20000)]);
        var plan = Plan(contour);
        var rules = Rules();
        var layout = SprinklerLayouter.Layout(plan, rules);
        layout.Issues.ShouldBeEmpty();
        layout.Sprinklers.Count.ShouldBe(54);
        LayoutChecks.ShouldSatisfy(layout, plan, rules, angle);
    }

    [Fact]
    public void Columns_are_avoided_without_extra_sprinklers()
    {
        // Колонны 400 × 400 с шагом 7,5 × 5 м: без обхода оросители (15; 5) и (15; 15) м попали бы в колонны.
        var columns = new List<Column>();
        for (var x = 7500; x < 30000; x += 7500)
        {
            for (var y = 5000; y < 20000; y += 5000)
            {
                columns.Add(new Column($"C{x}-{y}", Rect(x - 200, y - 200, x + 200, y + 200)));
            }
        }

        var plan = Plan(Rect(0, 0, 30000, 20000), columns);
        var rules = Rules();
        var layout = SprinklerLayouter.Layout(plan, rules);
        layout.Issues.ShouldBeEmpty();
        layout.Sprinklers.Count.ShouldBe(54);
        LayoutChecks.ShouldSatisfy(layout, plan, rules);
    }

    [Fact]
    public void Column_that_cannot_be_avoided_is_an_error_with_location()
    {
        // 2,4 × 2,4 м при пределе 1,2 м — единственное место оросителя в центре, а там колонна.
        var plan = Plan(Rect(0, 0, 2400, 2400), [new Column("C1", Rect(1000, 1000, 1400, 1400))]);
        var layout = SprinklerLayouter.Layout(plan, Rules(ConstructionClass.K2));
        layout.Sprinklers.ShouldBeEmpty();
        var issue = layout.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("layout.columnConflict");
        issue.Level.ShouldBe(IssueLevel.Error);
        issue.ElementId.ShouldBe("C1");
        issue.Location.ShouldBe(new PlanPoint(1200, 1200));
    }

    [Fact]
    public void No_sprinkler_zone_is_a_hole_with_walls()
    {
        var zone = new NoSprinklerZone("Z1", Rect(25000, 0, 30000, 4000));
        var plan = Plan(Rect(0, 0, 30000, 20000), zones: [zone]);
        var rules = Rules();
        var layout = SprinklerLayouter.Layout(plan, rules);
        layout.Issues.ShouldBeEmpty();
        LayoutChecks.ShouldSatisfy(layout, plan, rules);
    }

    [Fact]
    public void Non_orthogonal_room_is_not_laid_out()
    {
        var plan = Plan(Poly((0, 0), (20000, 0), (15000, 10000), (0, 10000)));
        var layout = SprinklerLayouter.Layout(plan, Rules());
        layout.Sprinklers.ShouldBeEmpty();
        var issue = layout.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("layout.nonOrthogonal");
        issue.Level.ShouldBe(IssueLevel.Error);
        issue.ElementId.ShouldBe("R1");
    }

    [Fact]
    public void High_beams_split_the_ceiling_into_fields_with_beam_faces_as_walls()
    {
        // 6.2.10: балка выше 0,3 м (К0) — оросители между балками; грани приняты как стены (В-17): до них не дальше 6.2.21.
        var high = new Beam("B1", Rect(0, 5900, 30000, 6100), new Length(0.35), new Length(-0.4));
        var low = new Beam("B2", Rect(0, 12900, 30000, 13100), new Length(0.25), new Length(-0.4));
        var plan = Plan(Rect(0, 0, 30000, 20000), beams: [high, low]);
        var rules = Rules();
        var layout = SprinklerLayouter.Layout(plan, rules);
        var issue = layout.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("layout.beamFields");
        issue.Level.ShouldBe(IssueLevel.Info);
        issue.ElementId.ShouldBe("B1");
        issue.Clause.ShouldBe("6.2.10");
        layout.Sprinklers.ShouldNotContain(s => high.Outline.Contains(s.Location));
        LayoutChecks.ShouldSatisfy(layout, plan, rules, extraWalls: [high.Outline]);

        // Поле 30 × 5,9 м и поле 30 × 13,9 м: ряды по обе стороны балки.
        layout.Sprinklers.ShouldContain(s => s.Location.YMm < 5900);
        layout.Sprinklers.ShouldContain(s => s.Location.YMm > 6100 && s.Location.YMm < 6100 + rules.MaxWallDistance.Millimeters + 1);

        // Для К2 предел 0,2 м — обе балки делят перекрытие.
        SprinklerLayouter.Layout(plan, Rules(ConstructionClass.K2)).Issues.Count(i => i.Code == "layout.beamFields").ShouldBe(2);
    }

    [Theory]
    [InlineData(14900, 15100)] // балка 200 мм
    [InlineData(14700, 15300)] // балка 600 мм: при 1,75 м до каждой грани зазор был бы 4,1 м > 3,5 м
    public void Spacing_across_a_beam_stays_within_the_norm_and_rows_stay_connected(double x0, double x1)
    {
        // Балка поперёк рядов: зазор между оросителями по обе стороны балки на одном ряду — не больше шага.
        var beam = new Beam("B1", Rect(x0, 0, x1, 20000), new Length(0.35), new Length(-0.4));
        var plan = Plan(Rect(0, 0, 30000, 20000), beams: [beam]) with { Inlets = [new Inlet("I1", new PlanPoint(500, 500), null)] };
        var rules = Rules();
        var layout = SprinklerLayouter.Layout(plan, rules);
        LayoutChecks.ShouldSatisfy(layout, plan, rules, extraWalls: [beam.Outline]);
        foreach (var row in layout.Sprinklers.GroupBy(s => Math.Round(s.Location.YMm)))
        {
            var left = row.Where(s => s.Location.XMm < x0).Max(s => s.Location.XMm);
            var right = row.Where(s => s.Location.XMm > x1).Min(s => s.Location.XMm);
            (right - left).ShouldBeLessThanOrEqualTo(rules.MaxSpacing.Millimeters + 0.5, $"ряд y = {row.Key}: через балку {right - left} мм");
        }

        var routing = WaterAuto.Calc.Routing.PipeRouter.Route(plan, layout, Norms, rules, new Pressure(0.3));
        routing.Issues.ShouldNotContain(i => i.Code == "routing.rowNotReached");
        routing.Routes.Single().Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler).ShouldBe(layout.Sprinklers.Count);
    }

    [Fact]
    public void Pipes_cross_beams_to_reach_every_field()
    {
        var high = new Beam("B1", Rect(0, 5900, 30000, 6100), new Length(0.35), new Length(-0.4));
        var plan = Plan(Rect(0, 0, 30000, 20000), beams: [high]) with { Inlets = [new Inlet("I1", new PlanPoint(500, 500), null)] };
        var layout = SprinklerLayouter.Layout(plan, Rules());
        var routing = WaterAuto.Calc.Routing.PipeRouter.Route(plan, layout, Norms, Rules(), new Pressure(0.3));
        routing.Issues.Where(i => i.Level == IssueLevel.Error).ShouldBeEmpty();
        routing.Routes.Single().Nodes.Count(n => n.Kind == RouteNodeKind.Sprinkler).ShouldBe(layout.Sprinklers.Count);
    }

    [Fact]
    public void Wide_ducts_are_reported_until_6_2_14_is_implemented()
    {
        var wide = new Duct("D1", Rect(2000, 15000, 12000, 16000), new Length(-0.9));
        var narrow = new Duct("D2", Rect(2000, 5000, 12000, 5500), new Length(-0.9));
        var plan = Plan(Rect(0, 0, 30000, 20000), ducts: [wide, narrow]);
        var issue = SprinklerLayouter.Layout(plan, Rules()).Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("layout.duct");
        issue.Level.ShouldBe(IssueLevel.Warning);
        issue.ElementId.ShouldBe("D1");
        issue.Clause.ShouldBe("6.2.14");
    }

    [Fact]
    public void Checks_catch_a_missing_or_misplaced_sprinkler()
    {
        // Проверка тестов: без оросителя в середине шаг вдвое больше нормы, у стены — дальше предела.
        var plan = Plan(Rect(0, 0, 30000, 20000));
        var rules = Rules();
        var layout = SprinklerLayouter.Layout(plan, rules);
        var gap = layout with { Sprinklers = layout.Sprinklers.Where((_, k) => k != 20).ToList() };
        Should.Throw<ShouldAssertException>(() => LayoutChecks.ShouldSatisfy(gap, plan, rules));

        var first = layout.Sprinklers[0];
        var moved = layout with { Sprinklers = [first with { Location = new PlanPoint(first.Location.XMm + 300, first.Location.YMm) }, .. layout.Sprinklers.Skip(1)] };
        Should.Throw<ShouldAssertException>(() => LayoutChecks.ShouldSatisfy(moved, plan, rules, lateralToleranceMm: 1));
    }

    [Fact]
    public void Layout_is_deterministic_and_ids_are_stable()
    {
        var plan = Plan(Rect(0, 0, 30000, 20000));
        var a = SprinklerLayouter.Layout(plan, Rules());
        var b = SprinklerLayouter.Layout(plan, Rules());
        a.Sprinklers.ShouldBe(b.Sprinklers);
        a.Sprinklers[0].Id.ShouldBe("R1.S1");
        a.Sprinklers.Select(s => s.Id).Distinct().Count().ShouldBe(54);
    }
}
