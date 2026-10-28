using Shouldly;

namespace WaterAuto.Application.Tests;

/// <summary>
/// Выпадающие списки исходных данных: значения и подписи берутся из data/ и не расходятся с нормами/каталогом.
/// </summary>
public sealed class InputCatalogTests
{
    private readonly string _norms = RepoPaths.Norms;

    [Fact]
    public void Room_groups_are_read_from_table_6_1_in_order()
    {
        var groups = InputCatalog.RoomGroups(_norms);

        groups.Select(g => g.Id).ShouldBe(["1", "2", "3", "4.1", "4.2", "5", "6", "7"]);
        var second = groups.Single(g => g.Id == "2");
        // Подпись собирается из величин норм, а не дублируется в коде.
        second.Label.ShouldContain("30");
        second.Label.ShouldContain("120");
        second.Label.ShouldContain("3,5");
    }

    [Fact]
    public void Sprinklers_are_the_water_general_purpose_catalog_rows()
    {
        var sprinklers = InputCatalog.Sprinklers(_norms);

        sprinklers.ShouldNotBeEmpty();
        sprinklers.ShouldContain(s => s.Model == "СВН-10" && s.K > 0);
        sprinklers.ShouldAllBe(s => s.Label.Length > 0 && s.K > 0);
    }

    [Fact]
    public void Pipe_standards_include_water_and_electric_welded_pipes()
    {
        var standards = InputCatalog.PipeStandards(_norms);

        standards.Select(s => s.Designation).ShouldContain("ГОСТ 3262-75");
        standards.Select(s => s.Designation).ShouldContain("ГОСТ 10704-91");
        standards.ShouldAllBe(s => s.Name.StartsWith("Трубы стальные", StringComparison.Ordinal));
    }

    [Fact]
    public void Pipe_dns_are_the_accepted_assortment()
    {
        InputCatalog.PipeDns(_norms).ShouldBe([50, 65, 80, 100, 125, 150]);
    }

    [Fact]
    public void Pump_sets_include_pump_counts_and_flow_head_limits()
    {
        var sets = InputCatalog.PumpSets(_norms);

        sets.Count.ShouldBeGreaterThan(50);
        sets.ShouldContain(s => s.MainPumps == 2 && s.StandbyPumps == 1);
        sets.ShouldAllBe(s => s.Label.Contains('+', StringComparison.Ordinal) && s.QMaxM3h > 0 && s.HMaxM > 0);
    }

    [Fact]
    public void Hydrant_rows_have_description_and_flow()
    {
        var rows = InputCatalog.HydrantRows(_norms);

        rows.Count.ShouldBeGreaterThan(10);
        rows.ShouldContain(r => r.Label.Contains("Жилые", StringComparison.Ordinal) && r.Jets > 0 && r.JetFlow > 0);
    }

    [Fact]
    public void Norms_info_has_document_and_edition()
    {
        var info = InputCatalog.Norms(_norms);

        info.ShouldNotBeNull();
        info.Document.ShouldBe("СП 485.1311500.2020");
        info.Edition.ShouldNotBeNullOrWhiteSpace();
    }
}
