using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using Shouldly;
using WaterAuto.Import;

namespace WaterAuto.Application.Tests;

/// <summary>
/// Проверка чертежа до расчёта: распознанная геометрия и предложения по исходным данным, выведенные из плана
/// (высота помещения и отметка труб — от низа перекрытия, H — от отметок, подводящий — по расстоянию).
/// </summary>
public sealed class DrawingPreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-preview-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Этаж 30 × 20 м, помещение насосной 12 × 8 м, два ввода, выпуск, два патрубка ГМ; балка с отметкой низа -1,2 м;
    // по желанию — тексты высотных отметок на слое ПТ_ОТМЕТКИ.
    private string Drawing(bool withPumpRoom = true, bool withBeam = true, params (double X, double Y, string Text)[] levelMarks)
    {
        var doc = new CadDocument(ACadVersion.AC1032);
        doc.Header.InsUnits = UnitsType.Millimeters;
        Layer L(string name)
        {
            if (!doc.Layers.TryGetValue(name, out var layer))
            {
                layer = new Layer(name);
                doc.Layers.Add(layer);
            }

            return layer;
        }

        LwPolyline Rect(double x0, double y0, double x1, double y1) =>
            new([new XY(x0, y0), new XY(x1, y0), new XY(x1, y1), new XY(x0, y1)]) { IsClosed = true };

        var floor = Rect(0, 0, 30000, 20000);
        floor.Layer = L(FloorPlanImporter.ContourLayer);
        doc.Entities.Add(floor);
        doc.Entities.Add(new TextEntity("Автостоянка") { InsertPoint = new XYZ(15000, 10000, 0), Height = 250, Layer = L(FloorPlanImporter.ContourLayer) });
        doc.Entities.Add(new Point(new XYZ(500, 500, 0)) { Layer = L(FloorPlanImporter.InletLayer) });
        if (withBeam)
        {
            var beam = Rect(1000, 4000, 29000, 4600);
            beam.Layer = L(FloorPlanImporter.BeamLayer);
            beam.Thickness = 400; // высота балки, мм
            beam.Elevation = -1200; // низ перекрытия, мм
            doc.Entities.Add(beam);
        }

        foreach (var (x, y, text) in levelMarks)
        {
            doc.Entities.Add(new TextEntity(text) { InsertPoint = new XYZ(x, y, 0), Height = 250, Layer = L(FloorPlanImporter.LevelMarkLayer) });
        }

        if (!withPumpRoom)
        {
            return Save(doc);
        }

        var room = Rect(1000, 1000, 13000, 9000);
        room.Layer = L(FloorPlanImporter.PumpRoomLayer);
        doc.Entities.Add(room);
        doc.Entities.Add(new Point(new XYZ(13000, 5000, 0)) { Layer = L(FloorPlanImporter.PumpRoomLayer) });
        foreach (var y in new[] { 3000.0, 5000.0 })
        {
            doc.Entities.Add(new Point(new XYZ(1000, y, 0)) { Layer = L(FloorPlanImporter.PumpInletLayer) });
        }

        doc.Entities.Add(new Point(new XYZ(13000, 3000, 0)) { Layer = L(FloorPlanImporter.PumpOutletLayer) });
        foreach (var y in new[] { 7000.0, 8000.0 })
        {
            doc.Entities.Add(new Point(new XYZ(13000, y, 0)) { Layer = L(FloorPlanImporter.PumpTruckLayer) });
        }

        return Save(doc);
    }

    private string Save(CadDocument doc)
    {
        var path = Path.Combine(_dir, "floor.dxf");
        DxfWriter.Write(path, doc, false);
        return path;
    }

    private static ProjectFile Project(double? floorElevation = -3, double pumpFloorElevation = -3) => new()
    {
        Name = "Проверка чертежа",
        Drawing = "floor.dxf",
        Design = new ProjectDesign
        {
            RoomGroup = "2",
            SprinklerK = 0.35,
            FloorElevation = floorElevation,
            HangerRodLength = 300,
            PumpRoom = new ProjectPumpRoom { FloorElevation = pumpFloorElevation, Height = 4.5 },
        },
    };

    [Fact]
    public void Recognizes_rooms_and_the_pump_room_on_the_plan()
    {
        var result = DrawingPreview.Analyze(Project(), Drawing(), RepoPaths.Norms);

        result.DrawingFound.ShouldBeTrue();
        result.HasErrors.ShouldBeFalse();
        result.Rooms.Count.ShouldBe(1);
        result.Rooms[0].Name.ShouldBe("Автостоянка");
        result.Rooms[0].AreaM2.ShouldBe(600);
        result.TotalProtectedAreaM2.ShouldBe(600);
        result.PumpRoomId.ShouldNotBeNull();
        result.PumpRoomLengthM.ShouldBe(12);
        result.PumpRoomWidthM.ShouldBe(8);
        result.PumpRoomAreaM2.ShouldBe(96);
        result.PumpRoomInlets.ShouldBe(2);
        result.PumpRoomOutlets.ShouldBe(1);
        result.TruckInlets.ShouldBe(2);
        result.PumpRoomHasDoor.ShouldBeTrue();
        result.Inlets.ShouldBe(1);
        result.Beams.ShouldBe(1);
        result.SlabBottomM.ShouldBe(-1.2);
    }

    [Fact]
    public void Suggests_room_height_pipe_elevation_and_pump_head_from_beam_bottom()
    {
        var result = DrawingPreview.Analyze(Project(), Drawing(), RepoPaths.Norms);
        var byKey = result.Suggestions.ToDictionary(s => s.Key);

        // Низ перекрытия -1,2 м; отметка пола -3 м.
        byKey["roomHeight"].Value.ShouldBe(1.8);
        // Отметка труб = низ перекрытия - шпилька 300 мм.
        byKey["pipeElevation"].Value.ShouldBe(-1.5);
        // H = отметка труб - (пол насосной + ось коллектора по каталогу).
        var axisMm = PumpCatalog.Sets(RepoPaths.Norms)
            .GroupBy(s => s.CollectorAxisMm)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .First().Key;
        byKey["dictatingAboveAxis"].Value.ShouldBe(-1.5 - (-3 + (axisMm / 1000.0)), 0.01);
        // Высота помещения насосной = низ перекрытия - отметка пола насосной.
        byKey["pumpRoom.height"].Value.ShouldBe(1.8);
        // Ближайшая точка ПТ_ВВОД в 0,7 м от контура насосной.
        byKey["supplyLength"].Value.ShouldBe(0.7, 0.01);
        byKey.Values.ShouldAllBe(s => s.Basis.Length > 0);
    }

    [Fact]
    public void Missing_drawing_is_reported_without_exception()
    {
        var result = DrawingPreview.Analyze(Project(), Path.Combine(_dir, "nope.dxf"), RepoPaths.Norms);

        result.DrawingFound.ShouldBeFalse();
        result.Issues.ShouldContain(i => i.Code == "preview.drawingMissing");
    }

    [Fact]
    public void Drawings_without_pump_room_or_beams_give_warnings_and_no_geometry_suggestions()
    {
        var noPumpRoom = DrawingPreview.Analyze(Project(), Drawing(withPumpRoom: false), RepoPaths.Norms);
        noPumpRoom.Issues.ShouldContain(i => i.Code == "preview.noPumpRoom" && i.Level == Core.IssueLevel.Warning);
        noPumpRoom.Suggestions.ShouldNotContain(s => s.Key == "supplyLength");
        noPumpRoom.Suggestions.ShouldNotContain(s => s.Key == "dictatingAboveAxis");

        var noBeam = DrawingPreview.Analyze(Project(), Drawing(withBeam: false), RepoPaths.Norms);
        noBeam.Suggestions.ShouldBeEmpty();
        noBeam.SlabBottomM.ShouldBeNull();
    }

    [Fact]
    public void Level_marks_suggest_floor_elevations_and_heights_are_recalculated_from_them()
    {
        // Отметки на слое ПТ_ОТМЕТКИ: у помещения −2,500, у насосной −3,000. В проекте пол этажа пуст, пол насосной 0 —
        // отметки должны прийти с чертежа, а высоты — считаться от них (низ перекрытия −1,2 м).
        var drawing = Drawing(levelMarks: [(15000, 10000, "-2,500"), (7000, 5000, "отм. -3,000")]);
        var result = DrawingPreview.Analyze(Project(floorElevation: null, pumpFloorElevation: 0), drawing, RepoPaths.Norms);
        var byKey = result.Suggestions.ToDictionary(s => s.Key);

        byKey["floorElevation"].Value.ShouldBe(-2.5, 0.001);
        byKey["floorElevation"].Basis.ShouldContain("ПТ_ОТМЕТКИ");
        byKey["floorElevation"].Basis.ShouldContain("помещением");
        byKey["pumpRoom.floorElevation"].Value.ShouldBe(-3, 0.001);
        byKey["pumpRoom.floorElevation"].Basis.ShouldContain("насосной");

        byKey["roomHeight"].Value.ShouldBe(1.3, 0.001);
        byKey["pumpRoom.height"].Value.ShouldBe(1.8, 0.001);
        var axisMm = PumpCatalog.Sets(RepoPaths.Norms)
            .GroupBy(s => s.CollectorAxisMm)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .First().Key;
        byKey["dictatingAboveAxis"].Value.ShouldBe(-1.5 - (-3 + (axisMm / 1000.0)), 0.01);
        // Отметка труб от отметок пола не зависит.
        byKey["pipeElevation"].Value.ShouldBe(-1.5);
    }
}
