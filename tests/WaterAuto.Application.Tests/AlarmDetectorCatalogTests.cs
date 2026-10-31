using WaterAuto.Application;
using WaterAuto.Core;

namespace WaterAuto.Application.Tests;

// Каталог извещателей: выбор модели в проекте, паспорт модели — в расчёт.
public class AlarmDetectorCatalogTests
{
    private static IReadOnlyList<DetectorModel> Catalog => AlarmDetectorCatalog.Load(EquipmentSet.PathFor(RepoPaths.Norms, AlarmDetectorCatalog.File));

    [Fact]
    public void Repository_catalog_has_smoke_heat_and_manual_models_with_passport_currents()
    {
        var catalog = Catalog;

        catalog.Single(m => m.Id == "bolid.dip34a_03").ShouldSatisfyAllConditions(
            m => m.Kind.ShouldBe(DetectorKind.Smoke),
            m => m.LoopCurrent.Amperes.ShouldBe(0.0006),
            m => m.Addresses.ShouldBe(1),
            m => m.Isolator.ShouldBeFalse());
        catalog.Single(m => m.Id == "bolid.s2000_ip_03").HeatClass.ShouldBe("A1R");
        catalog.Where(m => m.IsManual).ShouldNotBeEmpty();
    }

    [Fact]
    public void Requested_model_is_used_default_is_reported_and_mismatch_is_an_error()
    {
        var catalog = Catalog;
        var issues = new List<Issue>();

        AlarmDetectorCatalog.Choose(catalog, DetectorKind.Heat, "bolid.s2000_ip_03", "Склад", issues)!.Name.ShouldBe("С2000-ИП-03");
        issues.ShouldBeEmpty();

        AlarmDetectorCatalog.Choose(catalog, DetectorKind.Smoke, null, "Стоянка", issues)!.Id.ShouldBe("bolid.dip34a_03");
        issues.ShouldHaveSingleItem().Code.ShouldBe("aps.detector.default");

        AlarmDetectorCatalog.Choose(catalog, DetectorKind.Smoke, "bolid.s2000_ip_03", "Стоянка", issues).ShouldBeNull();
        issues.ShouldContain(i => i.Code == "aps.detector.kindMismatch" && i.Level == IssueLevel.Error);
        AlarmDetectorCatalog.Choose(catalog, DetectorKind.Smoke, "nope", "Стоянка", issues).ShouldBeNull();
        issues.ShouldContain(i => i.Code == "aps.detector.unknown");
    }
}
