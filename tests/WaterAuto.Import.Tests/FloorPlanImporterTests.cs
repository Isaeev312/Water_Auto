using System.Text;
using ACadSharp;
using ACadSharp.Types.Units;
using WaterAuto.Core;

namespace WaterAuto.Import.Tests;

public class FloorPlanImporterTests
{
    private static ImportResult ImportDxf(DrawingBuilder b) => FloorPlanImporter.Import(b.WriteDxf());

    private static Issue Single(ImportResult r, string code) =>
        r.Issues.Where(i => i.Code == code).ShouldHaveSingleItem(string.Join("; ", r.Issues.Select(i => $"{i.Code}: {i.Message}")));

    [Fact]
    public void Valid_floor_is_imported_with_all_layers()
    {
        var r = ImportDxf(DrawingBuilder.ValidFloor());

        r.HasErrors.ShouldBeFalse(string.Join("; ", r.Issues.Select(i => i.Message)));
        var plan = r.Plan!;
        plan.Rooms.ShouldHaveSingleItem().Name.ShouldBe("Автостоянка");
        plan.Rooms[0].Contour.AreaMm2.ShouldBe(600e6, 1);
        plan.Columns.Count.ShouldBe(1);
        plan.Beams.ShouldHaveSingleItem().Height.Meters.ShouldBe(0.35, 1e-9);
        plan.Beams[0].SlabBottom.Meters.ShouldBe(-0.4, 1e-9);
        plan.Ducts.ShouldHaveSingleItem().Bottom.Meters.ShouldBe(-0.9, 1e-9);
        plan.NoSprinklerZones.Count.ShouldBe(1);
        plan.Inlets.ShouldHaveSingleItem().Location.ShouldBe(new PlanPoint(1000, 1000));
        plan.Rooms[0].Id.ShouldStartWith("H");
    }

    [Fact]
    public void Dwg_file_is_read_the_same_way()
    {
        var r = FloorPlanImporter.Import(DrawingBuilder.ValidFloor().WriteDwg());

        r.HasErrors.ShouldBeFalse(string.Join("; ", r.Issues.Select(i => i.Message)));
        r.Plan!.Rooms.ShouldHaveSingleItem().Name.ShouldBe("Автостоянка");
        r.Plan.Beams.ShouldHaveSingleItem().Height.Meters.ShouldBe(0.35, 1e-9);
    }

    [Fact]
    public void Unclosed_contour_is_an_error_with_coordinates()
    {
        var b = DrawingBuilder.ValidFloor();
        b.Polyline(FloorPlanImporter.ContourLayer, false, (40000, 0), (50000, 0), (50000, 10000));

        var r = ImportDxf(b);

        r.Plan.ShouldBeNull();
        Single(r, "import.notClosed").Location.ShouldBe(new PlanPoint(40000, 0));
    }

    [Fact]
    public void Polyline_closed_by_repeated_vertex_is_accepted_with_info()
    {
        var b = new DrawingBuilder();
        b.Polyline(FloorPlanImporter.ContourLayer, false, (0, 0), (10000, 0), (10000, 10000), (0, 10000), (0, 0));
        b.Point(FloorPlanImporter.InletLayer, 500, 500);

        var r = ImportDxf(b);

        r.HasErrors.ShouldBeFalse();
        Single(r, "import.closedByVertex").Level.ShouldBe(IssueLevel.Info);
        r.Plan!.Rooms[0].Contour.Vertices.Count.ShouldBe(4);
    }

    [Fact]
    public void Column_entirely_outside_is_an_error_and_partly_outside_a_warning()
    {
        var outside = DrawingBuilder.ValidFloor();
        outside.Rect(FloorPlanImporter.ColumnLayer, 40000, 40000, 40400, 40400);
        var e = Single(ImportDxf(outside), "import.outsideContour");
        e.Level.ShouldBe(IssueLevel.Error);
        e.Location!.Value.XMm.ShouldBe(40200, 1);

        var inWall = DrawingBuilder.ValidFloor();
        inWall.Rect(FloorPlanImporter.ColumnLayer, -200, 9800, 200, 10200);
        var r = ImportDxf(inWall);
        Single(r, "import.partlyOutside").Level.ShouldBe(IssueLevel.Warning);
        r.Plan.ShouldNotBeNull();
    }

    [Fact]
    public void Beam_without_thickness_is_an_error()
    {
        var b = DrawingBuilder.ValidFloor();
        b.Rect(FloorPlanImporter.BeamLayer, 0, 11900, 30000, 12100);

        var e = Single(ImportDxf(b), "import.beamHeight");

        e.Clause.ShouldBe("6.2.10");
        e.Location!.Value.YMm.ShouldBe(12000, 1);
    }

    [Fact]
    public void Intersecting_and_nested_contours_are_errors()
    {
        var crossing = DrawingBuilder.ValidFloor();
        crossing.Rect(FloorPlanImporter.ContourLayer, 25000, 15000, 40000, 30000);
        Single(ImportDxf(crossing), "import.contoursIntersect");

        var nested = DrawingBuilder.ValidFloor();
        nested.Rect(FloorPlanImporter.ContourLayer, 5000, 5000, 8000, 8000);
        Single(ImportDxf(nested), "import.contourNested");
    }

    [Fact]
    public void Self_intersecting_contour_is_an_error()
    {
        var b = DrawingBuilder.ValidFloor();
        b.Polyline(FloorPlanImporter.ContourLayer, true, (40000, 0), (50000, 10000), (50000, 0), (40000, 10000)); // «бабочка»

        Single(ImportDxf(b), "import.selfIntersecting").Location.ShouldBe(new PlanPoint(40000, 0));
    }

    [Fact]
    public void Missing_inlet_is_an_error()
    {
        var b = new DrawingBuilder();
        b.Rect(FloorPlanImporter.ContourLayer, 0, 0, 10000, 10000);

        var r = ImportDxf(b);

        r.Plan.ShouldBeNull();
        Single(r, "import.noInlet");
    }

    [Fact]
    public void Drawing_not_in_millimetres_is_rejected()
    {
        var b = new DrawingBuilder(units: UnitsType.Meters);
        b.Rect(FloorPlanImporter.ContourLayer, 0, 0, 30, 20);
        b.Point(FloorPlanImporter.InletLayer, 1, 1);

        Single(ImportDxf(b), "import.units");
    }

    [Fact]
    public void Unsupported_entity_on_a_known_layer_is_not_dropped_silently()
    {
        var b = DrawingBuilder.ValidFloor();
        b.Line(FloorPlanImporter.ContourLayer, 0, 0, 5000, 5000);

        var e = Single(ImportDxf(b), "import.unsupportedEntity");

        e.Level.ShouldBe(IssueLevel.Error);
        e.Location.ShouldBe(new PlanPoint(0, 0));
    }

    [Fact]
    public void Layer_with_latin_lookalike_letters_is_reported()
    {
        var b = new DrawingBuilder();
        b.Rect(FloorPlanImporter.ContourLayer, 0, 0, 10000, 10000);
        b.Point("ПT_ВВОД", 500, 500); // латинская T

        var r = ImportDxf(b);

        Single(r, "import.unknownLayer").Message.ShouldContain("ПТ_ВВОД");
        Single(r, "import.noInlet");
    }

    [Fact]
    public void Arc_segments_are_tessellated()
    {
        // Полукруг радиусом 5 м на хорде 10 м: bulge = 1.
        var b = new DrawingBuilder();
        var pl = b.Polyline(FloorPlanImporter.ContourLayer, true, (0, 0), (10000, 0));
        pl.Vertices[0].Bulge = 1;
        b.Point(FloorPlanImporter.InletLayer, 5000, 1000);

        var r = ImportDxf(b);

        r.HasErrors.ShouldBeFalse(string.Join("; ", r.Issues.Select(i => i.Message)));
        var area = r.Plan!.Rooms[0].Contour.AreaMm2;
        area.ShouldBe(Math.PI * 5000 * 5000 / 2, Math.PI * 5000 * 5000 / 2 * 0.01);
    }

    [Fact]
    public void Column_block_is_placed_with_rotation_and_scale()
    {
        var b = DrawingBuilder.ValidFloor();
        // Блок 400×200 от начала координат, масштаб 2, поворот 90°: сечение 400 по X и 800 по Y от точки вставки.
        b.Block(FloorPlanImporter.ColumnLayer, "COL", 20000, 10000, Math.PI / 2, 2, (0, 0), (400, 0), (400, 200), (0, 200));

        var r = ImportDxf(b);

        r.HasErrors.ShouldBeFalse(string.Join("; ", r.Issues.Select(i => i.Message)));
        var block = r.Plan!.Columns.Single(c => c.Outline.Centroid.XMm > 15000);
        block.Outline.Centroid.XMm.ShouldBe(19800, 1);
        block.Outline.Centroid.YMm.ShouldBe(10400, 1);
        block.Outline.AreaMm2.ShouldBe(800 * 400, 1);
    }

    [Fact]
    public void Round_column_is_read_from_a_circle()
    {
        var b = DrawingBuilder.ValidFloor();
        b.Circle(FloorPlanImporter.ColumnLayer, 20000, 15000, 300);

        var r = ImportDxf(b);

        r.Plan!.Columns.Count.ShouldBe(2);
        r.Plan.Columns.Max(c => c.Outline.Centroid.XMm).ShouldBe(20000, 1);
    }

    [Fact]
    public void Cyrillic_layers_survive_an_AutoCAD_2000_file_in_code_page_1251()
    {
        var b = DrawingBuilder.ValidFloor();
        var doc = b.Document;
        doc.Header.Version = ACadVersion.AC1015;
        doc.Header.CodePage = "ANSI_1251";
        var path = b.WriteDxf();

        // Файл действительно в cp1251, а не в UTF-8: иначе тест ничего не проверяет.
        var bytes = File.ReadAllBytes(path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = Encoding.GetEncoding(1251).GetBytes(FloorPlanImporter.ContourLayer);
        var utf8 = Encoding.UTF8.GetBytes(FloorPlanImporter.ContourLayer);
        bytes.AsSpan().IndexOf(cp1251).ShouldBeGreaterThanOrEqualTo(0);
        bytes.AsSpan().IndexOf(utf8).ShouldBe(-1);

        var r = FloorPlanImporter.Import(path);

        r.HasErrors.ShouldBeFalse(string.Join("; ", r.Issues.Select(i => i.Message)));
        r.Plan!.Rooms.ShouldHaveSingleItem().Name.ShouldBe("Автостоянка");
    }
}
