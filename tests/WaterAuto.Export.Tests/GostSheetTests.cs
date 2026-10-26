using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using WaterAuto.Core;

namespace WaterAuto.Export.Tests;

public class GostSheetTests
{
    private static readonly TitleBlock Stamp = new("123-ПТ", "Автостоянка", "Здание", "Аксонометрическая схема", "Р", "Организация", [new("Разраб.", "Иванов")], Sheet: "5");

    [Fact]
    public void Small_scheme_goes_on_A3_at_the_largest_scale_that_fits()
    {
        // 30 × 15 м: на А3 поле над штампом 375 × 212 мм → 1:100 (300 × 150 мм), 1:75 не помещается (400 мм).
        var sheet = GostSheet.Fit(0, 0, 30000, 15000);
        sheet.Format.ShouldBe(SheetFormat.A3);
        sheet.Scale.ShouldBe(100);
    }

    [Fact]
    public void Large_scheme_moves_to_a_bigger_format()
    {
        var sheet = GostSheet.Fit(0, 0, 100000, 50000);
        sheet.Format.ShouldNotBe(SheetFormat.A3);
        sheet.Scale.ShouldBeLessThanOrEqualTo(200);
    }

    [Fact]
    public void Scheme_is_centred_in_the_field_above_the_title_block()
    {
        var sheet = GostSheet.Fit(1000, 2000, 31000, 17000);
        var cx = sheet.Origin.X + (((30 + 405) / 2.0) * sheet.Scale);
        var cy = sheet.Origin.Y + (((70 + 282) / 2.0) * sheet.Scale);
        cx.ShouldBe(16000, 1e-6);
        cy.ShouldBe(9500, 1e-6);
    }

    [Fact]
    public void Axonometry_with_a_stamp_has_frame_and_form_3()
    {
        var route = new PipeRoute(
            "R1",
            "I1",
            [new RouteNode("I1", RouteNodeKind.Inlet, new(0, 0)), new RouteNode("S1", RouteNodeKind.Sprinkler, new(30000, 0), "S1")],
            [new RouteSegment("P1", "I1", "S1", RouteSegmentRole.Supply, new Length(30), 1, 50)]);
        var path = Path.Combine(Path.GetTempPath(), $"wa-sheet-{Guid.NewGuid():N}.dxf");
        AxonometryDxfWriter.Write(path, [route], new Length(0), stamp: Stamp);
        CadDocument doc;
        try
        {
            doc = DxfReader.Read(path);
        }
        finally
        {
            File.Delete(path);
        }

        var sheet = doc.Entities.Where(e => e.Layer.Name == GostSheet.Layer).ToList();
        var texts = sheet.OfType<MText>().Select(t => t.Value).ToList();
        texts.ShouldContain("123-ПТ");
        texts.ShouldContain("Разраб.");
        texts.ShouldContain("Инв. № подл.");
        texts.ShouldContain("5");
        texts.ShouldContain(t => t.StartsWith("Формат А3", StringComparison.Ordinal));
        sheet.OfType<Line>().Count().ShouldBeGreaterThan(30);
        var labels = doc.Entities.Where(e => e.Layer.Name == AxonometryDxfWriter.LabelLayer).OfType<MText>().Select(t => t.Value).ToList();
        labels.ShouldContain("Условные обозначения");
        labels.ShouldContain("Ороситель спринклерный");
        labels.ShouldContain("Аксонометрическая схема"); // заголовок над схемой

        // 31 м с запасами на А3 — 1:100; подписи схемы — 2,5 мм на листе.
        doc.Entities.OfType<TextEntity>().Where(t => t.Layer.Name == AxonometryDxfWriter.LabelLayer).ShouldAllBe(t => Math.Abs(t.Height - 250) < 1e-6);
    }
}
